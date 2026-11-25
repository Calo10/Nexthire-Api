using nexthire_api.DTOs.CoreHr;

namespace nexthire_api.Services;

/// <summary>
/// Pure (database-free) onboarding rules: status transitions, task applicability, progress,
/// overdue counting, completion readiness and request validation.
/// </summary>
public static class OnboardingValidation
{
    // -------------------------------------------------------------------------
    // Normalization
    // -------------------------------------------------------------------------

    public static string? NormalizeStatus(string? value) => NormalizeFromSet(OnboardingStatuses.All, value);
    public static string? NormalizeTaskStatus(string? value) => NormalizeFromSet(OnboardingTaskStatuses.All, value);
    public static string? NormalizeCategory(string? value) => NormalizeFromSet(OnboardingTaskCategories.All, value);
    public static string? NormalizeAssignmentType(string? value) => NormalizeFromSet(OnboardingAssignmentTypes.All, value);
    public static string? NormalizeDocumentStatus(string? value) => NormalizeFromSet(OnboardingDocumentStatuses.All, value);

    /// <summary>Returns the canonical (stored) casing of <paramref name="value"/> in <paramref name="set"/>, or null.</summary>
    public static string? NormalizeFromSet(HashSet<string> set, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return set.TryGetValue(trimmed, out var canonical) ? canonical : null;
    }

    public static bool IsActiveStatus(string? status) =>
        !string.IsNullOrWhiteSpace(status) && OnboardingStatuses.Active.Contains(status.Trim());

    public static bool IsTerminalStatus(string? status) =>
        string.Equals(status?.Trim(), OnboardingStatuses.Completed, StringComparison.OrdinalIgnoreCase)
        || string.Equals(status?.Trim(), OnboardingStatuses.Cancelled, StringComparison.OrdinalIgnoreCase);

    // -------------------------------------------------------------------------
    // Status transitions
    // -------------------------------------------------------------------------

    private static readonly Dictionary<string, string[]> Transitions = new(StringComparer.OrdinalIgnoreCase)
    {
        // NotStarted -> InProgress (start)
        [OnboardingStatuses.NotStarted] = [OnboardingStatuses.InProgress, OnboardingStatuses.Cancelled],
        // InProgress -> InReview; InProgress -> Completed is the Kanban "drag to Completed" fast path
        // and is subject to exactly the same completion validation as InReview -> Completed.
        [OnboardingStatuses.InProgress] = [OnboardingStatuses.InReview, OnboardingStatuses.Completed, OnboardingStatuses.Cancelled],
        // InReview -> Completed, or back to InProgress
        [OnboardingStatuses.InReview] = [OnboardingStatuses.Completed, OnboardingStatuses.InProgress, OnboardingStatuses.Cancelled],
        [OnboardingStatuses.Completed] = [],
        [OnboardingStatuses.Cancelled] = []
    };

    /// <summary>Structural check only (does not look at tasks).</summary>
    public static bool CanTransition(string? from, string? to)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) return false;
        return Transitions.TryGetValue(from.Trim(), out var allowed)
               && allowed.Contains(to.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Full transition validation: structural rules plus task-based gates.
    /// Moving to InReview or Completed requires every required, applicable task to be satisfied.
    /// Cancelling requires a reason. Returns an error message, or null when the transition is allowed.
    /// </summary>
    public static string? ValidateTransition(
        string? from,
        string? to,
        IEnumerable<IOnboardingTaskState> tasks,
        string? cancelReason = null)
    {
        var fromStatus = NormalizeStatus(from);
        var toStatus = NormalizeStatus(to);
        if (fromStatus == null) return "Unknown current onboarding status.";
        if (toStatus == null)
            return $"Status must be one of: {string.Join(", ", OnboardingStatuses.All.OrderBy(s => s))}.";
        if (fromStatus.Equals(toStatus, StringComparison.OrdinalIgnoreCase))
            return $"The onboarding is already {toStatus}.";

        if (!CanTransition(fromStatus, toStatus))
        {
            if (IsTerminalStatus(fromStatus))
                return $"A {fromStatus} onboarding cannot change status.";
            return $"An onboarding cannot move from {fromStatus} to {toStatus}.";
        }

        if (toStatus == OnboardingStatuses.Cancelled && string.IsNullOrWhiteSpace(cancelReason))
            return "A cancellation reason is required.";

        if (toStatus is OnboardingStatuses.InReview or OnboardingStatuses.Completed)
        {
            var blockers = GetCompletionBlockers(tasks).Select(t => t.Title).ToList();
            if (blockers.Count > 0)
            {
                var shown = string.Join("; ", blockers.Take(5)) + (blockers.Count > 5 ? $"; and {blockers.Count - 5} more" : "");
                return $"Required tasks are not complete (or awaiting approval): {shown}.";
            }
        }

        return null;
    }

    // -------------------------------------------------------------------------
    // Task rules
    // -------------------------------------------------------------------------

    /// <summary>A task counts toward progress unless it is waived or cancelled.</summary>
    public static bool IsApplicable(IOnboardingTaskState task) =>
        !task.IsWaived && !string.Equals(task.Status, OnboardingTaskStatuses.Cancelled, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Completed, and — when the task requires approval — approved.
    /// A completed task awaiting (or denied) approval does not count.
    /// </summary>
    public static bool IsSatisfied(IOnboardingTaskState task)
    {
        if (!string.Equals(task.Status, OnboardingTaskStatuses.Completed, StringComparison.OrdinalIgnoreCase)) return false;
        if (!task.RequiresApproval) return true;
        return string.Equals(task.ApprovalStatus, OnboardingApprovalStatuses.Approved, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Applicable, not yet Completed and past its due date.</summary>
    public static bool IsOverdue(IOnboardingTaskState task, DateTime today)
    {
        if (!IsApplicable(task)) return false;
        if (string.Equals(task.Status, OnboardingTaskStatuses.Completed, StringComparison.OrdinalIgnoreCase)) return false;
        return task.DueDate.HasValue && task.DueDate.Value.Date < today.Date;
    }

    public static int CountOverdue(IEnumerable<IOnboardingTaskState> tasks, DateTime today) =>
        tasks.Count(t => IsOverdue(t, today));

    public static (int Completed, int Total) CountProgress(IEnumerable<IOnboardingTaskState> tasks)
    {
        var applicable = tasks.Where(IsApplicable).ToList();
        return (applicable.Count(IsSatisfied), applicable.Count);
    }

    /// <summary>completedApplicable / totalApplicable * 100, rounded to one decimal. No applicable tasks yields 0.</summary>
    public static double ComputeProgressPercent(IEnumerable<IOnboardingTaskState> tasks)
    {
        var (completed, total) = CountProgress(tasks);
        if (total == 0) return 0;
        return Math.Round(completed * 100.0 / total, 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>Required, applicable tasks that are not yet satisfied.</summary>
    public static IReadOnlyList<T> GetCompletionBlockers<T>(IEnumerable<T> tasks) where T : IOnboardingTaskState =>
        tasks.Where(t => t.IsRequired && IsApplicable(t) && !IsSatisfied(t)).ToList();

    public static bool CanComplete(IEnumerable<IOnboardingTaskState> tasks) =>
        GetCompletionBlockers(tasks).Count == 0;

    /// <summary>
    /// A task may only be completed once the task it depends on (if any, and still applicable) is satisfied.
    /// Returns the blocking task's title, or null when the task is free to complete.
    /// </summary>
    public static string? FindUnmetDependency(IOnboardingTaskState task, IEnumerable<IOnboardingTaskState> allTasks)
    {
        if (!task.DependsOnTaskId.HasValue) return null;
        var dependency = allTasks.FirstOrDefault(t => t.Id == task.DependsOnTaskId.Value);
        if (dependency == null || !IsApplicable(dependency) || IsSatisfied(dependency)) return null;
        return dependency.Title;
    }

    public static DateTime? ComputeDueDate(DateTime? startDate, int? offsetDays) =>
        startDate.HasValue && offsetDays.HasValue ? startDate.Value.Date.AddDays(offsetDays.Value) : null;

    /// <summary>Percent change versus the previous period; null when there is no baseline.</summary>
    public static double? PercentChange(double current, double previous) =>
        previous == 0 ? null : Math.Round((current - previous) / previous * 100.0, 1);

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

    public static List<string> ValidateTemplate(UpsertOnboardingTemplateRequest request)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.Name)) errors.Add("Template name is required.");
        else if (request.Name.Trim().Length > 200) errors.Add("Template name must be 200 characters or fewer.");
        if (request.Description is { Length: > 2000 }) errors.Add("Description must be 2000 characters or fewer.");
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

    public static List<string> ValidateCustomTask(CreateOnboardingTaskRequest t) =>
        ValidateTaskFields("Task", t.Title, t.Description, t.Category, t.AssignmentType, t.RequiresDocument, t.DocumentCategory, t.AssignedUserId);

    public static List<string> ValidateTaskUpdate(UpdateOnboardingTaskRequest t)
    {
        var errors = ValidateTaskFields("Task", t.Title, t.Description, t.Category, t.AssignmentType, t.RequiresDocument, t.DocumentCategory, t.AssignedUserId);
        if (!string.IsNullOrWhiteSpace(t.Status) && !OnboardingTaskStatuses.Settable.Contains(t.Status.Trim()))
            errors.Add("Task status can be set to Pending, InProgress or Blocked. Use the complete / reopen / cancel actions for other changes.");
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
            errors.Add($"{label}: category must be one of {string.Join(", ", OnboardingTaskCategories.All.OrderBy(c => c))}.");
        var assignment = NormalizeAssignmentType(assignmentType);
        if (assignment == null)
            errors.Add($"{label}: assignment type must be one of {string.Join(", ", OnboardingAssignmentTypes.All.OrderBy(c => c))}.");
        else if (assignment == OnboardingAssignmentTypes.SpecificUser && userId == null)
            errors.Add($"{label}: a specific user must be chosen for SpecificUser assignment.");
        if (documentCategory is { Length: > 80 }) errors.Add($"{label}: document category must be 80 characters or fewer.");
        if (requiresDocument && string.IsNullOrWhiteSpace(documentCategory))
            errors.Add($"{label}: a document category is required when a document is required.");
        return errors;
    }

    public static List<string> ValidateCreateOnboarding(CreateOnboardingRequest request)
    {
        var errors = new List<string>();
        if (request.EmployeeId == Guid.Empty) errors.Add("employeeId is required.");
        if (request.Notes is { Length: > 2000 }) errors.Add("Notes must be 2000 characters or fewer.");
        if (request.StartDate.HasValue && request.ExpectedCompletionDate.HasValue
            && request.ExpectedCompletionDate.Value.Date < request.StartDate.Value.Date)
            errors.Add("Expected completion date cannot be before the start date.");
        foreach (var task in request.AdditionalTasks ?? [])
            errors.AddRange(ValidateCustomTask(task));
        return errors;
    }

    public static List<string> ValidateUpdateOnboarding(UpdateOnboardingRequest request)
    {
        var errors = new List<string>();
        if (request.Notes is { Length: > 2000 }) errors.Add("Notes must be 2000 characters or fewer.");
        if (request.StartDate.HasValue && request.ExpectedCompletionDate.HasValue
            && request.ExpectedCompletionDate.Value.Date < request.StartDate.Value.Date)
            errors.Add("Expected completion date cannot be before the start date.");
        return errors;
    }
}
