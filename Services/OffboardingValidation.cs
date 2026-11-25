using System.Text.Json;
using nexthire_api.DTOs.CoreHr;

namespace nexthire_api.Services;

/// <summary>
/// Pure (database-free) offboarding rules: status transitions, task applicability, progress, overdue counting,
/// clearance / completion readiness (tasks, assets, access, settlement) and request validation.
/// </summary>
public static class OffboardingValidation
{
    // -------------------------------------------------------------------------
    // Normalization
    // -------------------------------------------------------------------------

    public static string? NormalizeStatus(string? value) => NormalizeFromSet(OffboardingStatuses.All, value);
    public static string? NormalizeSeparationType(string? value) => NormalizeFromSet(SeparationTypes.All, value);
    public static string? NormalizeRehireEligibility(string? value) => NormalizeFromSet(RehireEligibilities.All, value);
    public static string? NormalizeTaskStatus(string? value) => NormalizeFromSet(OffboardingTaskStatuses.All, value);
    public static string? NormalizeCategory(string? value) => NormalizeFromSet(OffboardingTaskCategories.All, value);
    public static string? NormalizeAssignmentType(string? value) => NormalizeFromSet(OffboardingAssignmentTypes.All, value);
    public static string? NormalizeDocumentStatus(string? value) => NormalizeFromSet(OffboardingDocumentStatuses.All, value);
    public static string? NormalizeAssetStatus(string? value) => NormalizeFromSet(OffboardingAssetStatuses.All, value);
    public static string? NormalizeAccessStatus(string? value) => NormalizeFromSet(OffboardingAccessStatuses.All, value);
    public static string? NormalizeSettlementStatus(string? value) => NormalizeFromSet(OffboardingSettlementStatuses.All, value);
    public static string? NormalizeExitInterviewStatus(string? value) => NormalizeFromSet(OffboardingExitInterviewStatuses.All, value);

    /// <summary>Returns the canonical (stored) casing of <paramref name="value"/> in <paramref name="set"/>, or null.</summary>
    public static string? NormalizeFromSet(HashSet<string> set, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return set.TryGetValue(trimmed, out var canonical) ? canonical : null;
    }

    public static bool IsActiveStatus(string? status) =>
        !string.IsNullOrWhiteSpace(status) && OffboardingStatuses.Active.Contains(status.Trim());

    public static bool IsTerminalStatus(string? status) =>
        string.Equals(status?.Trim(), OffboardingStatuses.Completed, StringComparison.OrdinalIgnoreCase)
        || string.Equals(status?.Trim(), OffboardingStatuses.Cancelled, StringComparison.OrdinalIgnoreCase);

    // -------------------------------------------------------------------------
    // Employee notification policy
    // -------------------------------------------------------------------------

    /// <summary>Employer-initiated separations (involuntary termination, layoff).</summary>
    public static bool IsInvoluntary(string? separationType) =>
        !string.IsNullOrWhiteSpace(separationType) && SeparationTypes.Involuntary.Contains(separationType.Trim());

    /// <summary>
    /// The employee may only be notified when it was explicitly requested (notify_employee) and the separation is not involuntary.
    /// </summary>
    public static bool CanNotifyEmployee(string? separationType, bool notifyEmployeeFlag) =>
        notifyEmployeeFlag && !IsInvoluntary(separationType);

    /// <summary>Validation message when an explicit opt-in conflicts with an involuntary separation; otherwise null.</summary>
    public static string? ValidateNotifyEmployee(string? separationType, bool notifyEmployeeFlag) =>
        notifyEmployeeFlag && IsInvoluntary(separationType)
            ? "The employee cannot be notified automatically about an involuntary separation."
            : null;

    // -------------------------------------------------------------------------
    // Status transitions
    // -------------------------------------------------------------------------

    private static readonly Dictionary<string, string[]> Transitions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Scheduled -> InProgress (start)
        [OffboardingStatuses.Scheduled] = [OffboardingStatuses.InProgress, OffboardingStatuses.Cancelled],
        // InProgress -> PendingClearance (submit clearance); InProgress -> Completed is the Kanban "drag to Completed"
        // fast path and is subject to exactly the same clearance + completion validation as PendingClearance -> Completed.
        [OffboardingStatuses.InProgress] = [OffboardingStatuses.PendingClearance, OffboardingStatuses.Completed, OffboardingStatuses.Cancelled],
        // PendingClearance -> Completed, or back to InProgress
        [OffboardingStatuses.PendingClearance] = [OffboardingStatuses.Completed, OffboardingStatuses.InProgress, OffboardingStatuses.Cancelled],
        [OffboardingStatuses.Completed] = [],
        [OffboardingStatuses.Cancelled] = []
    };

    /// <summary>Structural check only (does not look at tasks, assets, access or settlement).</summary>
    public static bool CanTransition(string? from, string? to)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) return false;
        return Transitions.TryGetValue(from.Trim(), out var allowed)
               && allowed.Contains(to.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Full transition validation: structural rules plus gates.
    /// PendingClearance requires every required, applicable task to be satisfied (completed and approved where needed).
    /// Completed additionally requires required assets Returned/Waived, access Verified/NotApplicable/Revoked and
    /// required settlement items Completed/Waived/NotApplicable. Cancelling requires a reason.
    /// Returns an error message, or null when the transition is allowed.
    /// </summary>
    public static string? ValidateTransition(
        string? from,
        string? to,
        IEnumerable<IOffboardingTaskState> tasks,
        string? cancelReason = null,
        IEnumerable<IOffboardingAssetState>? assets = null,
        IEnumerable<IOffboardingAccessState>? accessItems = null,
        IEnumerable<IOffboardingSettlementState>? settlementItems = null)
    {
        var fromStatus = NormalizeStatus(from);
        var toStatus = NormalizeStatus(to);
        if (fromStatus == null) return "Unknown current offboarding status.";
        if (toStatus == null)
            return $"Status must be one of: {string.Join(", ", OffboardingStatuses.All.OrderBy(s => s))}.";
        if (fromStatus.Equals(toStatus, StringComparison.OrdinalIgnoreCase))
            return $"The offboarding is already {toStatus}.";

        if (!CanTransition(fromStatus, toStatus))
        {
            if (IsTerminalStatus(fromStatus))
                return $"A {fromStatus} offboarding cannot change status.";
            return $"An offboarding cannot move from {fromStatus} to {toStatus}.";
        }

        if (toStatus == OffboardingStatuses.Cancelled && string.IsNullOrWhiteSpace(cancelReason))
            return "A cancellation reason is required.";

        if (toStatus == OffboardingStatuses.PendingClearance)
        {
            var blockers = GetClearanceBlockers(tasks).Select(t => t.Title).ToList();
            if (blockers.Count > 0) return TaskBlockerMessage(blockers);
        }
        else if (toStatus == OffboardingStatuses.Completed)
        {
            var messages = GetCompletionBlockerMessages(tasks, assets ?? [], accessItems ?? [], settlementItems ?? []);
            if (messages.Count > 0) return string.Join(" ", messages);
        }

        return null;
    }

    private static string TaskBlockerMessage(IReadOnlyList<string> titles) =>
        $"Required tasks are not complete (or awaiting approval): {Summarize(titles)}.";

    private static string Summarize(IReadOnlyList<string> items) =>
        string.Join("; ", items.Take(5)) + (items.Count > 5 ? $"; and {items.Count - 5} more" : "");

    // -------------------------------------------------------------------------
    // Task rules
    // -------------------------------------------------------------------------

    /// <summary>A task counts toward progress unless it is waived or cancelled.</summary>
    public static bool IsApplicable(IOffboardingTaskState task) =>
        !task.IsWaived && !string.Equals(task.Status, OffboardingTaskStatuses.Cancelled, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Completed, and — when the task requires approval — approved.
    /// A completed task awaiting (or denied) approval does not count.
    /// </summary>
    public static bool IsSatisfied(IOffboardingTaskState task)
    {
        if (!string.Equals(task.Status, OffboardingTaskStatuses.Completed, StringComparison.OrdinalIgnoreCase)) return false;
        if (!task.RequiresApproval) return true;
        return string.Equals(task.ApprovalStatus, OffboardingApprovalStatuses.Approved, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Applicable, not yet Completed and past its due date.</summary>
    public static bool IsOverdue(IOffboardingTaskState task, DateTime today)
    {
        if (!IsApplicable(task)) return false;
        if (string.Equals(task.Status, OffboardingTaskStatuses.Completed, StringComparison.OrdinalIgnoreCase)) return false;
        return task.DueDate.HasValue && task.DueDate.Value.Date < today.Date;
    }

    public static int CountOverdue(IEnumerable<IOffboardingTaskState> tasks, DateTime today) =>
        tasks.Count(t => IsOverdue(t, today));

    public static (int Completed, int Total) CountProgress(IEnumerable<IOffboardingTaskState> tasks)
    {
        var applicable = tasks.Where(IsApplicable).ToList();
        return (applicable.Count(IsSatisfied), applicable.Count);
    }

    /// <summary>completedApplicable / totalApplicable * 100, rounded to one decimal. No applicable tasks yields 0.</summary>
    public static double ComputeProgressPercent(IEnumerable<IOffboardingTaskState> tasks)
    {
        var (completed, total) = CountProgress(tasks);
        if (total == 0) return 0;
        return Math.Round(completed * 100.0 / total, 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>Required, applicable tasks that are not yet satisfied (blocks submission for clearance).</summary>
    public static IReadOnlyList<T> GetClearanceBlockers<T>(IEnumerable<T> tasks) where T : IOffboardingTaskState =>
        tasks.Where(t => t.IsRequired && IsApplicable(t) && !IsSatisfied(t)).ToList();

    public static bool CanSubmitClearance(IEnumerable<IOffboardingTaskState> tasks) =>
        GetClearanceBlockers(tasks).Count == 0;

    // -------------------------------------------------------------------------
    // Completion gates (assets / access / settlement)
    // -------------------------------------------------------------------------

    /// <summary>A required asset is cleared once it is Returned or Waived (Damaged / Missing still block).</summary>
    public static bool IsAssetCleared(IOffboardingAssetState asset) =>
        !asset.ReturnRequired
        || string.Equals(asset.Status, OffboardingAssetStatuses.Returned, StringComparison.OrdinalIgnoreCase)
        || string.Equals(asset.Status, OffboardingAssetStatuses.Waived, StringComparison.OrdinalIgnoreCase);

    /// <summary>Access is cleared when Revoked, Verified or NotApplicable (Pending / Scheduled still block).</summary>
    public static bool IsAccessCleared(IOffboardingAccessState item) =>
        string.Equals(item.Status, OffboardingAccessStatuses.Revoked, StringComparison.OrdinalIgnoreCase)
        || string.Equals(item.Status, OffboardingAccessStatuses.Verified, StringComparison.OrdinalIgnoreCase)
        || string.Equals(item.Status, OffboardingAccessStatuses.NotApplicable, StringComparison.OrdinalIgnoreCase);

    /// <summary>A required settlement item is cleared when Completed, Waived or NotApplicable.</summary>
    public static bool IsSettlementCleared(IOffboardingSettlementState item) =>
        !item.IsRequired
        || string.Equals(item.Status, OffboardingSettlementStatuses.Completed, StringComparison.OrdinalIgnoreCase)
        || string.Equals(item.Status, OffboardingSettlementStatuses.Waived, StringComparison.OrdinalIgnoreCase)
        || string.Equals(item.Status, OffboardingSettlementStatuses.NotApplicable, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<T> GetAssetBlockers<T>(IEnumerable<T> assets) where T : IOffboardingAssetState =>
        assets.Where(a => !IsAssetCleared(a)).ToList();

    public static IReadOnlyList<T> GetAccessBlockers<T>(IEnumerable<T> items) where T : IOffboardingAccessState =>
        items.Where(i => !IsAccessCleared(i)).ToList();

    public static IReadOnlyList<T> GetSettlementBlockers<T>(IEnumerable<T> items) where T : IOffboardingSettlementState =>
        items.Where(i => !IsSettlementCleared(i)).ToList();

    /// <summary>One human-readable message per failing gate (empty when everything is cleared).</summary>
    public static List<string> GetCompletionBlockerMessages(
        IEnumerable<IOffboardingTaskState> tasks,
        IEnumerable<IOffboardingAssetState> assets,
        IEnumerable<IOffboardingAccessState> accessItems,
        IEnumerable<IOffboardingSettlementState> settlementItems)
    {
        var messages = new List<string>();

        var taskTitles = GetClearanceBlockers(tasks).Select(t => t.Title).ToList();
        if (taskTitles.Count > 0) messages.Add(TaskBlockerMessage(taskTitles));

        var assetNames = GetAssetBlockers(assets).Select(a => a.AssetName).ToList();
        if (assetNames.Count > 0)
            messages.Add($"Required assets are not returned or waived: {Summarize(assetNames)}.");

        var systems = GetAccessBlockers(accessItems).Select(i => i.SystemName).ToList();
        if (systems.Count > 0)
            messages.Add($"System access is not revoked or verified: {Summarize(systems)}.");

        var settlement = GetSettlementBlockers(settlementItems).Select(i => i.Title).ToList();
        if (settlement.Count > 0)
            messages.Add($"Required settlement items are not completed or waived: {Summarize(settlement)}.");

        return messages;
    }

    /// <summary>Short labels for everything still blocking completion (used by the detail view).</summary>
    public static List<string> GetCompletionBlockerLabels(
        IEnumerable<IOffboardingTaskState> tasks,
        IEnumerable<IOffboardingAssetState> assets,
        IEnumerable<IOffboardingAccessState> accessItems,
        IEnumerable<IOffboardingSettlementState> settlementItems)
    {
        var labels = new List<string>();
        labels.AddRange(GetClearanceBlockers(tasks).Select(t => $"Task: {t.Title}"));
        labels.AddRange(GetAssetBlockers(assets).Select(a => $"Asset: {a.AssetName}"));
        labels.AddRange(GetAccessBlockers(accessItems).Select(i => $"Access: {i.SystemName}"));
        labels.AddRange(GetSettlementBlockers(settlementItems).Select(i => $"Settlement: {i.Title}"));
        return labels;
    }

    public static bool CanComplete(
        IEnumerable<IOffboardingTaskState> tasks,
        IEnumerable<IOffboardingAssetState> assets,
        IEnumerable<IOffboardingAccessState> accessItems,
        IEnumerable<IOffboardingSettlementState> settlementItems) =>
        GetCompletionBlockerMessages(tasks, assets, accessItems, settlementItems).Count == 0;

    /// <summary>
    /// A task may only be completed once the task it depends on (if any, and still applicable) is satisfied.
    /// Returns the blocking task's title, or null when the task is free to complete.
    /// </summary>
    public static string? FindUnmetDependency(IOffboardingTaskState task, IEnumerable<IOffboardingTaskState> allTasks)
    {
        if (!task.DependsOnTaskId.HasValue) return null;
        var dependency = allTasks.FirstOrDefault(t => t.Id == task.DependsOnTaskId.Value);
        if (dependency == null || !IsApplicable(dependency) || IsSatisfied(dependency)) return null;
        return dependency.Title;
    }

    /// <summary>Due date = last working day + offset (offsets are usually negative: days before departure).</summary>
    public static DateTime? ComputeDueDate(DateTime? lastWorkingDay, int? offsetDays) =>
        lastWorkingDay.HasValue && offsetDays.HasValue ? lastWorkingDay.Value.Date.AddDays(offsetDays.Value) : null;

    /// <summary>Percent change versus the previous period; null when there is no baseline.</summary>
    public static double? PercentChange(double current, double previous) =>
        previous == 0 ? null : Math.Round((current - previous) / previous * 100.0, 1);

    // -------------------------------------------------------------------------
    // Access / asset / settlement / exit interview rules
    // -------------------------------------------------------------------------

    /// <summary>Verifying access only makes sense after it was revoked.</summary>
    public static string? ValidateAccessStatusChange(string? from, string? to)
    {
        var toStatus = NormalizeAccessStatus(to);
        if (toStatus == null)
            return $"Access status must be one of: {string.Join(", ", OffboardingAccessStatuses.All.OrderBy(s => s))}.";
        var fromStatus = NormalizeAccessStatus(from);
        if (fromStatus != null && fromStatus.Equals(toStatus, StringComparison.OrdinalIgnoreCase))
            return $"The access item is already {toStatus}.";
        if (toStatus == OffboardingAccessStatuses.Verified && fromStatus != OffboardingAccessStatuses.Revoked)
            return "Access must be revoked before it can be verified.";
        return null;
    }

    public static List<string> ValidateAssetStatusChange(UpdateOffboardingAssetStatusRequest request, DateTime? today = null)
    {
        var errors = new List<string>();
        var status = NormalizeAssetStatus(request.Status);
        if (status == null)
            errors.Add($"Asset status must be one of: {string.Join(", ", OffboardingAssetStatuses.All.OrderBy(s => s))}.");
        if (status == OffboardingAssetStatuses.Waived && string.IsNullOrWhiteSpace(request.WaiveReason))
            errors.Add("A reason is required to waive an asset return.");
        if (request.WaiveReason is { Length: > 1000 }) errors.Add("Waive reason must be 1000 characters or fewer.");
        if (request.ConditionNotes is { Length: > 500 }) errors.Add("Condition notes must be 500 characters or fewer.");
        if (request.Notes is { Length: > 1000 }) errors.Add("Notes must be 1000 characters or fewer.");
        if (request.EvidenceUrl is { Length: > 1000 }) errors.Add("Evidence URL must be 1000 characters or fewer.");
        if (request.ActualReturnDate.HasValue && request.ActualReturnDate.Value.Date > (today ?? DateTime.UtcNow).Date)
            errors.Add("The return date cannot be in the future.");
        return errors;
    }

    public static List<string> ValidateAssetFields(string? name, string? type, string? serial, string? notes)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(name)) errors.Add("Asset name is required.");
        else if (name.Trim().Length > 200) errors.Add("Asset name must be 200 characters or fewer.");
        if (type is { Length: > 80 }) errors.Add("Asset type must be 80 characters or fewer.");
        if (serial is { Length: > 120 }) errors.Add("Serial number must be 120 characters or fewer.");
        if (notes is { Length: > 1000 }) errors.Add("Notes must be 1000 characters or fewer.");
        return errors;
    }

    public static List<string> ValidateAccessFields(string? systemName, string? notes)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(systemName)) errors.Add("System name is required.");
        else if (systemName.Trim().Length > 200) errors.Add("System name must be 200 characters or fewer.");
        if (notes is { Length: > 1000 }) errors.Add("Notes must be 1000 characters or fewer.");
        return errors;
    }

    public static List<string> ValidateAccessStatusRequest(UpdateOffboardingAccessStatusRequest request)
    {
        var errors = new List<string>();
        if (NormalizeAccessStatus(request.Status) == null)
            errors.Add($"Access status must be one of: {string.Join(", ", OffboardingAccessStatuses.All.OrderBy(s => s))}.");
        if (request.Notes is { Length: > 1000 }) errors.Add("Notes must be 1000 characters or fewer.");
        if (request.EvidenceUrl is { Length: > 1000 }) errors.Add("Evidence URL must be 1000 characters or fewer.");
        return errors;
    }

    public static List<string> ValidateSettlementUpdate(UpdateOffboardingSettlementItemRequest request)
    {
        var errors = new List<string>();
        if (NormalizeSettlementStatus(request.Status) == null)
            errors.Add($"Settlement status must be one of: {string.Join(", ", OffboardingSettlementStatuses.All.OrderBy(s => s))}.");
        if (request.Amount is < 0) errors.Add("Amount cannot be negative.");
        if (request.Amount is > 999_999_999_999m) errors.Add("Amount is too large.");
        if (!string.IsNullOrWhiteSpace(request.Currency))
        {
            var c = request.Currency.Trim();
            if (c.Length != 3 || !c.All(char.IsLetter)) errors.Add("Currency must be a 3-letter code.");
        }
        if (request.Notes is { Length: > 1000 }) errors.Add("Notes must be 1000 characters or fewer.");
        if (request.EvidenceUrl is { Length: > 1000 }) errors.Add("Evidence URL must be 1000 characters or fewer.");
        return errors;
    }

    public static List<string> ValidateExitInterview(UpdateOffboardingExitInterviewRequest request)
    {
        var errors = new List<string>();
        var status = NormalizeExitInterviewStatus(request.Status);
        if (status == null)
            errors.Add($"Exit interview status must be one of: {string.Join(", ", OffboardingExitInterviewStatuses.All.OrderBy(s => s))}.");
        if (status == OffboardingExitInterviewStatuses.Scheduled && request.ScheduledAt == null)
            errors.Add("A scheduled date is required for a scheduled exit interview.");
        if (!IsValidJsonOrEmpty(request.QuestionnaireJson)) errors.Add("Questionnaire must be valid JSON.");
        if (!IsValidJsonOrEmpty(request.ResponsesJson)) errors.Add("Responses must be valid JSON.");
        return errors;
    }

    public static bool IsValidJsonOrEmpty(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return true;
        try
        {
            using var _ = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Request validation
    // -------------------------------------------------------------------------

    /// <summary>True when the dependency graph (task id -> depends-on id) contains a cycle or a self reference.</summary>
    public static bool HasDependencyCycle(IReadOnlyDictionary<Guid, Guid?> dependsOn)
    {
        foreach (var start in dependsOn.Keys)
        {
            var seen = new HashSet<Guid> { start };
            var current = dependsOn[start];
            while (current.HasValue)
            {
                if (!seen.Add(current.Value)) return true;
                current = dependsOn.TryGetValue(current.Value, out var next) ? next : null;
            }
        }
        return false;
    }

    public static List<string> ValidateTemplate(UpsertOffboardingTemplateRequest request)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.Name)) errors.Add("Template name is required.");
        else if (request.Name.Trim().Length > 200) errors.Add("Template name must be 200 characters or fewer.");
        if (request.Description is { Length: > 2000 }) errors.Add("Description must be 2000 characters or fewer.");
        if (!string.IsNullOrWhiteSpace(request.SeparationType) && NormalizeSeparationType(request.SeparationType) == null)
            errors.Add($"Separation type must be one of {string.Join(", ", SeparationTypes.All.OrderBy(s => s))}.");
        if (request.EstimatedDurationDays is < 0 or > 3650) errors.Add("Estimated duration must be between 0 and 3650 days.");
        if (request.IsDefault && !request.IsActive) errors.Add("The default template must be active.");

        var tasks = request.Tasks ?? [];
        if (tasks.Count > 200) errors.Add("A template can have at most 200 tasks.");

        var ids = new HashSet<Guid>();
        for (var i = 0; i < tasks.Count; i++)
        {
            var t = tasks[i];
            var label = $"Task {i + 1}";
            errors.AddRange(ValidateTaskFields(label, t.Title, t.Description, t.Category, t.AssignmentType,
                t.RequiresDocument, t.DocumentCategory, t.DefaultAssigneeUserId));
            if (t.DueOffsetDays is < -365 or > 3650) errors.Add($"{label}: due offset must be between -365 and 3650 days.");
            if (t.Id.HasValue && t.Id.Value != Guid.Empty && !ids.Add(t.Id.Value))
                errors.Add($"{label}: duplicate task id.");
        }

        var graph = new Dictionary<Guid, Guid?>();
        foreach (var t in tasks.Where(t => t.Id.HasValue && t.Id.Value != Guid.Empty))
        {
            if (t.DependsOnTaskId.HasValue)
            {
                if (t.DependsOnTaskId == t.Id) errors.Add($"Task '{t.Title}' cannot depend on itself.");
                else if (!ids.Contains(t.DependsOnTaskId.Value))
                    errors.Add($"Task '{t.Title}' depends on a task that is not part of this template.");
            }
            graph[t.Id!.Value] = t.DependsOnTaskId;
        }
        if (errors.Count == 0 && HasDependencyCycle(graph))
            errors.Add("Task dependencies contain a cycle.");

        foreach (var t in tasks.Where(t => !(t.Id.HasValue && t.Id.Value != Guid.Empty) && t.DependsOnTaskId.HasValue))
            errors.Add($"Task '{t.Title}' has a dependency but no id for other tasks to reference; give tasks ids to use dependencies.");

        return errors;
    }

    public static List<string> ValidateCustomTask(CreateOffboardingTaskRequest t) =>
        ValidateTaskFields("Task", t.Title, t.Description, t.Category, t.AssignmentType, t.RequiresDocument, t.DocumentCategory, t.AssignedUserId);

    public static List<string> ValidateTaskUpdate(UpdateOffboardingTaskRequest t)
    {
        var errors = ValidateTaskFields("Task", t.Title, t.Description, t.Category, t.AssignmentType, t.RequiresDocument, t.DocumentCategory, t.AssignedUserId);
        if (!string.IsNullOrWhiteSpace(t.Status) && !OffboardingTaskStatuses.Settable.Contains(t.Status.Trim()))
            errors.Add("Task status can be set to Pending, InProgress or Blocked. Use the complete / reopen / cancel actions for other changes.");
        if (t.IsWaived == true && string.IsNullOrWhiteSpace(t.WaiveReason))
            errors.Add("A reason is required to waive a task.");
        if (t.WaiveReason is { Length: > 1000 }) errors.Add("Waive reason must be 1000 characters or fewer.");
        return errors;
    }

    private static List<string> ValidateTaskFields(
        string label,
        string? title,
        string? description,
        string? category,
        string? assignmentType,
        bool requiresDocument,
        string? documentCategory,
        Guid? userId)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(title)) errors.Add($"{label}: title is required.");
        else if (title.Trim().Length > 300) errors.Add($"{label}: title must be 300 characters or fewer.");
        if (description is { Length: > 2000 }) errors.Add($"{label}: description must be 2000 characters or fewer.");
        if (NormalizeCategory(category) == null)
            errors.Add($"{label}: category must be one of {string.Join(", ", OffboardingTaskCategories.All.OrderBy(c => c))}.");
        var assignment = NormalizeAssignmentType(assignmentType);
        if (assignment == null)
            errors.Add($"{label}: assignment type must be one of {string.Join(", ", OffboardingAssignmentTypes.All.OrderBy(c => c))}.");
        else if (assignment == OffboardingAssignmentTypes.SpecificUser && userId == null)
            errors.Add($"{label}: a specific user must be chosen for SpecificUser assignment.");
        if (documentCategory is { Length: > 80 }) errors.Add($"{label}: document category must be 80 characters or fewer.");
        if (requiresDocument && string.IsNullOrWhiteSpace(documentCategory))
            errors.Add($"{label}: a document category is required when a document is required.");
        return errors;
    }

    /// <summary>Notice date, last working day and termination effective date must be in a sensible order.</summary>
    public static List<string> ValidateSeparationDates(DateTime? noticeDate, DateTime? lastWorkingDay, DateTime? terminationEffectiveDate)
    {
        var errors = new List<string>();
        if (noticeDate.HasValue && lastWorkingDay.HasValue && lastWorkingDay.Value.Date < noticeDate.Value.Date)
            errors.Add("The last working day cannot be before the notice date.");
        if (lastWorkingDay.HasValue && terminationEffectiveDate.HasValue
            && terminationEffectiveDate.Value.Date < lastWorkingDay.Value.Date)
            errors.Add("The termination effective date cannot be before the last working day.");
        if (noticeDate.HasValue && terminationEffectiveDate.HasValue
            && terminationEffectiveDate.Value.Date < noticeDate.Value.Date)
            errors.Add("The termination effective date cannot be before the notice date.");
        return errors;
    }

    /// <summary>The effective termination date defaults to the last working day.</summary>
    public static DateTime? ResolveTerminationEffectiveDate(DateTime? terminationEffectiveDate, DateTime? lastWorkingDay) =>
        (terminationEffectiveDate ?? lastWorkingDay)?.Date;

    /// <summary>True when the termination date has arrived (today or earlier) and should flip the employee to Terminated.</summary>
    public static bool IsTerminationDue(DateTime? terminationEffectiveDate, DateTime today) =>
        terminationEffectiveDate.HasValue && terminationEffectiveDate.Value.Date <= today.Date;

    public static List<string> ValidateCreateOffboarding(CreateOffboardingRequest request)
    {
        var errors = new List<string>();
        if (request.EmployeeId == Guid.Empty) errors.Add("employeeId is required.");

        var separationType = NormalizeSeparationType(request.SeparationType);
        if (string.IsNullOrWhiteSpace(request.SeparationType)) errors.Add("separationType is required.");
        else if (separationType == null)
            errors.Add($"Separation type must be one of: {string.Join(", ", SeparationTypes.All.OrderBy(s => s))}.");

        if (request.SeparationReason is { Length: > 1000 }) errors.Add("Separation reason must be 1000 characters or fewer.");
        if (request.ConfidentialNotes is { Length: > 20000 }) errors.Add("Confidential notes must be 20000 characters or fewer.");
        if (!string.IsNullOrWhiteSpace(request.RehireEligibility) && NormalizeRehireEligibility(request.RehireEligibility) == null)
            errors.Add($"Rehire eligibility must be one of: {string.Join(", ", RehireEligibilities.All.OrderBy(s => s))}.");

        errors.AddRange(ValidateSeparationDates(request.NoticeDate, request.LastWorkingDay,
            ResolveTerminationEffectiveDate(request.TerminationEffectiveDate, request.LastWorkingDay)));

        var notifyError = ValidateNotifyEmployee(separationType, request.NotifyEmployee);
        if (notifyError != null) errors.Add(notifyError);

        foreach (var task in request.AdditionalTasks ?? [])
            errors.AddRange(ValidateCustomTask(task));
        foreach (var asset in request.Assets ?? [])
            errors.AddRange(ValidateAssetFields(asset.AssetName, asset.AssetType, asset.SerialNumber, asset.Notes));
        foreach (var system in request.AccessSystems ?? [])
            errors.AddRange(ValidateAccessFields(system, null));
        return errors;
    }

    public static List<string> ValidateStartForEmployee(StartOffboardingForEmployeeRequest request)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.SeparationType)) errors.Add("separationType is required.");
        else if (NormalizeSeparationType(request.SeparationType) == null)
            errors.Add($"Separation type must be one of: {string.Join(", ", SeparationTypes.All.OrderBy(s => s))}.");
        return errors;
    }

    /// <summary>Field-level validation; the service additionally checks the notify flag against the effective separation type.</summary>
    public static List<string> ValidateUpdateOffboarding(UpdateOffboardingRequest request)
    {
        var errors = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.SeparationType) && NormalizeSeparationType(request.SeparationType) == null)
            errors.Add($"Separation type must be one of: {string.Join(", ", SeparationTypes.All.OrderBy(s => s))}.");
        if (request.SeparationReason is { Length: > 1000 }) errors.Add("Separation reason must be 1000 characters or fewer.");
        if (request.ConfidentialNotes is { Length: > 20000 }) errors.Add("Confidential notes must be 20000 characters or fewer.");
        if (!string.IsNullOrWhiteSpace(request.RehireEligibility) && NormalizeRehireEligibility(request.RehireEligibility) == null)
            errors.Add($"Rehire eligibility must be one of: {string.Join(", ", RehireEligibilities.All.OrderBy(s => s))}.");
        errors.AddRange(ValidateSeparationDates(request.NoticeDate, request.LastWorkingDay,
            ResolveTerminationEffectiveDate(request.TerminationEffectiveDate, request.LastWorkingDay)));
        return errors;
    }
}
