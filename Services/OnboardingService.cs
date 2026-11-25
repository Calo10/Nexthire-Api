using System.Collections.Concurrent;
using System.Data.SqlClient;
using System.Text.Json;
using nexthire_api.DTOs;
using nexthire_api.DTOs.CoreHr;
using nexthire_api.Exceptions;
using nexthire_api.Repositories;
using nexthire_api.Security;

namespace nexthire_api.Services;

public class OnboardingService : IOnboardingService
{
    // Orgs whose onboarding defaults (templates, notification settings, dev demo data) this process already ensured.
    private static readonly ConcurrentDictionary<Guid, bool> OrgInitialized = new();

    private readonly IOnboardingRepository _repo;
    private readonly IEmployeeRepository _employees;
    private readonly IRoleRepository _roles;
    private readonly IOnboardingNotificationService _notifications;
    private readonly IHostEnvironment _env;
    private readonly ILogger<OnboardingService> _logger;

    public OnboardingService(
        IOnboardingRepository repo,
        IEmployeeRepository employees,
        IRoleRepository roles,
        IOnboardingNotificationService notifications,
        IHostEnvironment env,
        ILogger<OnboardingService> logger)
    {
        _repo = repo;
        _employees = employees;
        _roles = roles;
        _notifications = notifications;
        _env = env;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // Permissions / readiness
    // -------------------------------------------------------------------------

    private async Task<IReadOnlyList<string>> RoleCodesAsync(HrActor actor) =>
        await _roles.ListRoleCodesByNexaUserAsync(actor.OrganizationId, actor.UserId);

    private async Task<IReadOnlyList<string>> RequireAsync(HrActor actor, string capability)
    {
        var codes = await RoleCodesAsync(actor);
        if (!OnboardingPermissions.Has(codes, capability))
            throw new HrAccessDeniedException(capability);
        return codes;
    }

    public async Task<IReadOnlyList<string>> GetCapabilitiesAsync(HrActor actor, CancellationToken cancellationToken = default) =>
        OnboardingPermissions.ListFor(await RoleCodesAsync(actor));

    private async Task EnsureOrgReadyAsync(Guid organizationId, Guid? actorUserId, CancellationToken cancellationToken)
    {
        if (OrgInitialized.ContainsKey(organizationId)) return;

        // Demo onboardings need demo employees; both seeders are idempotent and development-only for demo data.
        await _employees.EnsureOrgDefaultsAsync(organizationId, cancellationToken);
        await _employees.SeedDemoEmployeesIfEmptyAsync(organizationId, actorUserId, _env.IsDevelopment(), cancellationToken);
        await _repo.EnsureOrgDefaultsAsync(organizationId, cancellationToken);
        await _repo.SeedDemoOnboardingsIfEmptyAsync(organizationId, actorUserId, _env.IsDevelopment(), cancellationToken);
        OrgInitialized[organizationId] = true;
    }

    // -------------------------------------------------------------------------
    // Onboardings: read
    // -------------------------------------------------------------------------

    public async Task<PagedResult<OnboardingListItemDto>> ListAsync(HrActor actor, OnboardingListQuery query, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OnboardingPermissions.View);
        await EnsureOrgReadyAsync(actor.OrganizationId, actor.UserId, cancellationToken);

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            query.Status = OnboardingValidation.NormalizeStatus(query.Status)
                           ?? throw EmployeeServiceException.Validation(
                               $"Status filter must be one of: {string.Join(", ", OnboardingStatuses.All.OrderBy(s => s))}.");
        }
        else
        {
            query.Status = null;
        }
        query.Page = Math.Max(1, query.Page);
        query.PageSize = query.PageSize <= 0 ? 25 : Math.Min(query.PageSize, 200);
        query.Dir = string.Equals(query.Dir, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";
        query.Sort = string.IsNullOrWhiteSpace(query.Sort) ? "startDate" : query.Sort.Trim();
        return await _repo.ListAsync(actor.OrganizationId, query, cancellationToken);
    }

    public async Task<OnboardingPipelineDto> GetPipelineAsync(HrActor actor, OnboardingPipelineQuery query, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OnboardingPermissions.View);
        await EnsureOrgReadyAsync(actor.OrganizationId, actor.UserId, cancellationToken);
        if (query.CompletedWithinDays <= 0) query.CompletedWithinDays = 60;
        return await _repo.PipelineAsync(actor.OrganizationId, query, cancellationToken);
    }

    public async Task<OnboardingSummaryDto> GetSummaryAsync(HrActor actor, DateTime? from, DateTime? to, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OnboardingPermissions.View);
        await EnsureOrgReadyAsync(actor.OrganizationId, actor.UserId, cancellationToken);
        return await _repo.GetSummaryAsync(actor.OrganizationId, from, to, cancellationToken);
    }

    public async Task<OnboardingDetailDto?> GetAsync(HrActor actor, Guid onboardingId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OnboardingPermissions.View);
        return await _repo.GetAsync(actor.OrganizationId, onboardingId, cancellationToken);
    }

    public async Task<IReadOnlyList<OnboardingActivityDto>> GetActivityAsync(HrActor actor, Guid onboardingId, int take, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OnboardingPermissions.View);
        await RequireHeaderAsync(actor.OrganizationId, onboardingId, cancellationToken);
        return await _repo.ListActivityAsync(actor.OrganizationId, onboardingId, take <= 0 ? 50 : take, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Onboardings: create / update
    // -------------------------------------------------------------------------

    public async Task<OnboardingDetailDto> CreateAsync(HrActor actor, CreateOnboardingRequest request, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, OnboardingPermissions.Create);
        await EnsureOrgReadyAsync(actor.OrganizationId, actor.UserId, cancellationToken);

        // Strip operations the caller is not allowed to perform instead of failing the whole request.
        if (!OnboardingPermissions.Has(codes, OnboardingPermissions.ManageTasks))
        {
            request.AdditionalTasks = null;
            request.ExcludedTemplateTaskIds = null;
        }

        var errors = OnboardingValidation.ValidateCreateOnboarding(request);
        if (errors.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", errors));

        var employee = await _repo.GetEmployeeInfoAsync(actor.OrganizationId, request.EmployeeId, cancellationToken)
                       ?? throw EmployeeServiceException.NotFound("Employee not found.");
        if (employee.IsArchived)
            throw EmployeeServiceException.Conflict("Archived employees cannot be onboarded. Restore the employee first.", "employee_archived");
        if (await _repo.FindActiveOnboardingIdAsync(actor.OrganizationId, request.EmployeeId, cancellationToken) != null)
            throw EmployeeServiceException.Conflict("This employee already has an active onboarding.", "active_onboarding_exists");

        await EnsureUserInOrgAsync(actor.OrganizationId, request.OwnerUserId, "Owner", cancellationToken);
        foreach (var userId in (request.AdditionalTasks ?? []).Select(t => t.AssignedUserId).Where(id => id.HasValue).Select(id => id!.Value).Distinct())
            await EnsureUserInOrgAsync(actor.OrganizationId, userId, "Assignee", cancellationToken);

        Guid? templateId;
        if (request.TemplateId.HasValue)
        {
            var template = await _repo.GetTemplateAsync(actor.OrganizationId, request.TemplateId.Value, cancellationToken)
                           ?? throw EmployeeServiceException.NotFound("Template not found.");
            if (template.IsArchived || !template.IsActive)
                throw EmployeeServiceException.Validation("The selected template is not active.", "template_inactive");
            templateId = template.Id;
        }
        else
        {
            templateId = await _repo.ResolveTemplateIdForEmployeeAsync(actor.OrganizationId, request.EmployeeId, cancellationToken);
        }

        var today = DateTime.UtcNow.Date;
        var startDate = request.StartDate?.Date ?? employee.HireDate?.Date ?? (request.StartImmediately ? today : null);

        var command = new CreateOnboardingCommand
        {
            EmployeeId = request.EmployeeId,
            TemplateId = templateId,
            OwnerUserId = request.OwnerUserId,
            StartDate = startDate,
            ExpectedCompletionDate = request.ExpectedCompletionDate?.Date,
            Notes = request.Notes,
            InitialStatus = request.StartImmediately ? OnboardingStatuses.InProgress : OnboardingStatuses.NotStarted,
            ExcludedTemplateTaskIds = request.ExcludedTemplateTaskIds ?? [],
            AdditionalTasks = (request.AdditionalTasks ?? []).Select(ToSpec).ToList(),
            ActorUserId = actor.UserId,
            ActorName = actor.Name
        };

        CreatedOnboarding created;
        try
        {
            created = await _repo.CreateOnboardingAsync(actor.OrganizationId, command, cancellationToken);
        }
        catch (SqlException ex) when (IsUniqueViolation(ex))
        {
            throw EmployeeServiceException.Conflict("This employee already has an active onboarding.", "active_onboarding_exists");
        }

        await _repo.LinkAtsTasksAsync(actor.OrganizationId, created.Id, cancellationToken);

        Notify(actor, created.Id, OnboardingTriggers.OnboardingCreated);
        if (request.StartImmediately)
        {
            Notify(actor, created.Id, OnboardingTriggers.OnboardingStarted);
            await NotifyTaskAssignmentsAsync(actor, created.Id, cancellationToken);
        }

        return await _repo.GetAsync(actor.OrganizationId, created.Id, cancellationToken)
               ?? throw EmployeeServiceException.NotFound("Onboarding was created but could not be loaded.");
    }

    public async Task<OnboardingDetailDto> StartOnboardingForEmployeeAsync(
        HrActor actor, Guid employeeId, StartOnboardingForEmployeeRequest? request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OnboardingPermissions.Create);
        await EnsureOrgReadyAsync(actor.OrganizationId, actor.UserId, cancellationToken);

        var existingId = await _repo.FindActiveOnboardingIdAsync(actor.OrganizationId, employeeId, cancellationToken);
        if (existingId.HasValue)
        {
            return await _repo.GetAsync(actor.OrganizationId, existingId.Value, cancellationToken)
                   ?? throw EmployeeServiceException.NotFound("Onboarding not found.");
        }

        request ??= new StartOnboardingForEmployeeRequest();
        try
        {
            return await CreateAsync(actor, new CreateOnboardingRequest
            {
                EmployeeId = employeeId,
                TemplateId = request.TemplateId,
                OwnerUserId = request.OwnerUserId,
                StartDate = request.StartDate,
                StartImmediately = request.StartImmediately
            }, cancellationToken);
        }
        catch (EmployeeServiceException ex) when (ex.Code == "active_onboarding_exists")
        {
            // Lost a race with a concurrent call: stay idempotent and return the winner.
            var id = await _repo.FindActiveOnboardingIdAsync(actor.OrganizationId, employeeId, cancellationToken);
            if (id.HasValue)
            {
                var detail = await _repo.GetAsync(actor.OrganizationId, id.Value, cancellationToken);
                if (detail != null) return detail;
            }
            throw;
        }
    }

    public async Task<OnboardingDetailDto?> UpdateAsync(HrActor actor, Guid onboardingId, UpdateOnboardingRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OnboardingPermissions.Edit);

        var header = await _repo.GetHeaderAsync(actor.OrganizationId, onboardingId, cancellationToken);
        if (header == null) return null;
        RequireActive(header);

        var errors = OnboardingValidation.ValidateUpdateOnboarding(request);
        if (errors.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", errors));
        await EnsureUserInOrgAsync(actor.OrganizationId, request.OwnerUserId, "Owner", cancellationToken);

        if (!await _repo.UpdateAsync(actor.OrganizationId, onboardingId, request, actor.UserId, cancellationToken)) return null;

        var changes = new List<string>();
        if (header.OwnerUserId != request.OwnerUserId) changes.Add("owner");
        if (header.StartDate?.Date != request.StartDate?.Date) changes.Add("start date");
        if (header.ExpectedCompletionDate?.Date != request.ExpectedCompletionDate?.Date) changes.Add("expected completion date");
        await _repo.AddActivityAsync(actor.OrganizationId, onboardingId, "Updated", "Onboarding details updated",
            changes.Count > 0 ? JsonSerializer.Serialize(new { changed = changes }) : null, actor.UserId, actor.Name, cancellationToken);

        return await _repo.GetAsync(actor.OrganizationId, onboardingId, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Status transitions
    // -------------------------------------------------------------------------

    public async Task<OnboardingDetailDto?> StartAsync(HrActor actor, Guid onboardingId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OnboardingPermissions.Edit);
        return await ChangeStatusCoreAsync(actor, onboardingId, OnboardingStatuses.InProgress, null, null, null, cancellationToken);
    }

    public async Task<OnboardingDetailDto?> SubmitForReviewAsync(HrActor actor, Guid onboardingId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OnboardingPermissions.Edit);
        return await ChangeStatusCoreAsync(actor, onboardingId, OnboardingStatuses.InReview, null, null, null, cancellationToken);
    }

    public async Task<OnboardingDetailDto?> ReturnToProgressAsync(HrActor actor, Guid onboardingId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OnboardingPermissions.Edit);
        var header = await _repo.GetHeaderAsync(actor.OrganizationId, onboardingId, cancellationToken);
        if (header == null) return null;
        if (!header.Status.Equals(OnboardingStatuses.InReview, StringComparison.OrdinalIgnoreCase))
            throw EmployeeServiceException.Conflict("Only an onboarding in review can be returned to In Progress.", "invalid_transition");
        return await ChangeStatusCoreAsync(actor, onboardingId, OnboardingStatuses.InProgress, null, null, null, cancellationToken);
    }

    public async Task<OnboardingDetailDto?> CompleteAsync(HrActor actor, Guid onboardingId, CompleteOnboardingRequest? request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OnboardingPermissions.Complete);
        return await ChangeStatusCoreAsync(actor, onboardingId, OnboardingStatuses.Completed, null,
            request?.Notes, request?.CompletionDate, cancellationToken);
    }

    public async Task<OnboardingDetailDto?> CancelAsync(HrActor actor, Guid onboardingId, CancelOnboardingRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OnboardingPermissions.Cancel);
        return await ChangeStatusCoreAsync(actor, onboardingId, OnboardingStatuses.Cancelled, request.Reason, null, null, cancellationToken);
    }

    public async Task<OnboardingDetailDto?> ChangeStatusAsync(HrActor actor, Guid onboardingId, ChangeOnboardingStatusRequest request, CancellationToken cancellationToken = default)
    {
        var target = OnboardingValidation.NormalizeStatus(request.Status)
                     ?? throw EmployeeServiceException.Validation(
                         $"Status must be one of: {string.Join(", ", OnboardingStatuses.All.OrderBy(s => s))}.");

        // Each destination requires its own capability so dragging cards cannot bypass Complete / Cancel permissions.
        var capability = target switch
        {
            OnboardingStatuses.Completed => OnboardingPermissions.Complete,
            OnboardingStatuses.Cancelled => OnboardingPermissions.Cancel,
            _ => OnboardingPermissions.Edit
        };
        await RequireAsync(actor, capability);

        return await ChangeStatusCoreAsync(actor, onboardingId, target, request.Reason, request.Notes, null, cancellationToken);
    }

    private async Task<OnboardingDetailDto?> ChangeStatusCoreAsync(
        HrActor actor, Guid onboardingId, string target, string? reason, string? notes, DateTime? completionDate, CancellationToken cancellationToken)
    {
        var header = await _repo.GetHeaderAsync(actor.OrganizationId, onboardingId, cancellationToken);
        if (header == null) return null;
        var from = header.Status;

        if (from.Equals(target, StringComparison.OrdinalIgnoreCase))
            throw EmployeeServiceException.Conflict($"The onboarding is already {target}.", "invalid_transition");
        if (!OnboardingValidation.CanTransition(from, target))
        {
            throw EmployeeServiceException.Conflict(
                OnboardingValidation.IsTerminalStatus(from)
                    ? $"A {from} onboarding cannot change status."
                    : $"An onboarding cannot move from {from} to {target}.",
                "invalid_transition");
        }
        if (target == OnboardingStatuses.Cancelled && string.IsNullOrWhiteSpace(reason))
            throw EmployeeServiceException.Validation("A cancellation reason is required.", "reason_required");
        if (reason is { Length: > 1000 })
            throw EmployeeServiceException.Validation("The reason must be 1000 characters or fewer.");

        var today = DateTime.UtcNow.Date;
        DateTime? completion = null;
        if (target == OnboardingStatuses.Completed)
        {
            completion = (completionDate ?? today).Date;
            if (completion > today)
                throw EmployeeServiceException.Validation("The completion date cannot be in the future.");
            if (header.StartDate.HasValue && completion < header.StartDate.Value.Date)
                throw EmployeeServiceException.Validation("The completion date cannot be before the start date.");
        }

        // Single source of truth for the task gates (InReview and Completed — including drag-to-Completed).
        var tasks = await _repo.ListTasksAsync(actor.OrganizationId, onboardingId, cancellationToken);
        var error = OnboardingValidation.ValidateTransition(from, target, tasks, reason);
        if (error != null) throw EmployeeServiceException.Conflict(error, "required_tasks_incomplete");

        var applied = await _repo.TransitionStatusAsync(
            actor.OrganizationId, onboardingId, from, target, completion, reason, actor.UserId, cancellationToken);
        if (!applied)
            throw EmployeeServiceException.Conflict("The onboarding status changed in the meantime. Refresh and try again.", "status_conflict");

        var summary = target switch
        {
            OnboardingStatuses.InProgress when from == OnboardingStatuses.NotStarted => "Onboarding started",
            OnboardingStatuses.InProgress => "Onboarding returned to In Progress",
            OnboardingStatuses.InReview => "Onboarding submitted for review",
            OnboardingStatuses.Completed => "Onboarding completed",
            OnboardingStatuses.Cancelled => "Onboarding cancelled",
            _ => $"Status changed from {from} to {target}"
        };
        await _repo.AddActivityAsync(actor.OrganizationId, onboardingId, "StatusChanged", summary,
            JsonSerializer.Serialize(new { from, to = target, reason = NullIfEmpty(reason), notes = NullIfEmpty(notes) }),
            actor.UserId, actor.Name, cancellationToken);

        switch (target)
        {
            case OnboardingStatuses.InProgress when from == OnboardingStatuses.NotStarted:
                Notify(actor, onboardingId, OnboardingTriggers.OnboardingStarted);
                await NotifyTaskAssignmentsAsync(actor, onboardingId, cancellationToken);
                break;
            case OnboardingStatuses.InReview:
                Notify(actor, onboardingId, OnboardingTriggers.OnboardingInReview);
                break;
            case OnboardingStatuses.Completed:
                Notify(actor, onboardingId, OnboardingTriggers.OnboardingCompleted);
                break;
            case OnboardingStatuses.Cancelled:
                Notify(actor, onboardingId, OnboardingTriggers.OnboardingCancelled);
                break;
        }

        if (target == OnboardingStatuses.InProgress && from == OnboardingStatuses.NotStarted)
            await _repo.LinkAtsTasksAsync(actor.OrganizationId, onboardingId, cancellationToken);

        return await _repo.GetAsync(actor.OrganizationId, onboardingId, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Tasks
    // -------------------------------------------------------------------------

    public async Task<OnboardingTaskDto> AddTaskAsync(HrActor actor, Guid onboardingId, CreateOnboardingTaskRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OnboardingPermissions.ManageTasks);
        var header = await RequireHeaderAsync(actor.OrganizationId, onboardingId, cancellationToken);
        RequireActive(header);

        var errors = OnboardingValidation.ValidateCustomTask(request);
        if (errors.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", errors));
        await EnsureUserInOrgAsync(actor.OrganizationId, request.AssignedUserId, "Assignee", cancellationToken);

        if (request.DependsOnTaskId.HasValue
            && await _repo.GetTaskAsync(actor.OrganizationId, onboardingId, request.DependsOnTaskId.Value, cancellationToken) == null)
            throw EmployeeServiceException.Validation("The task this one depends on was not found in this onboarding.");

        var taskId = await _repo.AddTaskAsync(actor.OrganizationId, onboardingId, ToSpec(request), cancellationToken);
        await _repo.AddActivityAsync(actor.OrganizationId, onboardingId, "TaskAdded", $"Task added: {request.Title.Trim()}",
            JsonSerializer.Serialize(new { taskId }), actor.UserId, actor.Name, cancellationToken);

        await _repo.LinkAtsTasksAsync(actor.OrganizationId, onboardingId, cancellationToken);
        if (request.AssignedUserId.HasValue && header.Status == OnboardingStatuses.InProgress)
            Notify(actor, onboardingId, OnboardingTriggers.TaskAssigned, OnboardingNotificationKeys.ForTask(OnboardingTriggers.TaskAssigned, taskId, request.AssignedUserId), taskId);

        await ReturnToProgressIfNoLongerCompleteAsync(actor, header, cancellationToken);

        return await _repo.GetTaskAsync(actor.OrganizationId, onboardingId, taskId, cancellationToken)
               ?? throw EmployeeServiceException.NotFound("Task was created but could not be loaded.");
    }

    public async Task<OnboardingTaskDto?> UpdateTaskAsync(HrActor actor, Guid onboardingId, Guid taskId, UpdateOnboardingTaskRequest request, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, OnboardingPermissions.ManageTasks);
        var header = await RequireHeaderAsync(actor.OrganizationId, onboardingId, cancellationToken);
        RequireActive(header);

        var existing = await _repo.GetTaskAsync(actor.OrganizationId, onboardingId, taskId, cancellationToken);
        if (existing == null) return null;
        if (existing.Status == OnboardingTaskStatuses.Cancelled)
            throw EmployeeServiceException.Conflict("Cancelled tasks cannot be edited.", "task_cancelled");

        var errors = OnboardingValidation.ValidateTaskUpdate(request);
        if (errors.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", errors));
        await EnsureUserInOrgAsync(actor.OrganizationId, request.AssignedUserId, "Assignee", cancellationToken);

        // Waiving a task or toggling its approval requirement can bypass approvals: approvers only.
        var canApprove = OnboardingPermissions.Has(codes, OnboardingPermissions.ApproveTasks);
        var requiresApproval = canApprove && request.RequiresApproval.HasValue ? request.RequiresApproval.Value : existing.RequiresApproval;
        var isWaived = canApprove ? request.IsWaived : null;

        var spec = ToSpec(request);
        spec.RequiresApproval = requiresApproval;
        var status = OnboardingValidation.NormalizeFromSet(OnboardingTaskStatuses.Settable, request.Status);

        if (!await _repo.UpdateTaskAsync(actor.OrganizationId, onboardingId, taskId, spec, status, isWaived, cancellationToken))
            return null;

        await _repo.AddActivityAsync(actor.OrganizationId, onboardingId, "TaskUpdated", $"Task updated: {spec.Title.Trim()}",
            JsonSerializer.Serialize(new { taskId, waived = isWaived }), actor.UserId, actor.Name, cancellationToken);

        await _repo.LinkAtsTasksAsync(actor.OrganizationId, onboardingId, cancellationToken);
        if (request.AssignedUserId.HasValue && request.AssignedUserId != existing.AssignedUserId && header.Status == OnboardingStatuses.InProgress)
            Notify(actor, onboardingId, OnboardingTriggers.TaskAssigned, OnboardingNotificationKeys.ForTask(OnboardingTriggers.TaskAssigned, taskId, request.AssignedUserId), taskId);

        await ReturnToProgressIfNoLongerCompleteAsync(actor, header, cancellationToken);
        return await _repo.GetTaskAsync(actor.OrganizationId, onboardingId, taskId, cancellationToken);
    }

    public async Task<bool> CancelTaskAsync(HrActor actor, Guid onboardingId, Guid taskId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OnboardingPermissions.ManageTasks);
        var header = await RequireHeaderAsync(actor.OrganizationId, onboardingId, cancellationToken);
        RequireActive(header);

        var existing = await _repo.GetTaskAsync(actor.OrganizationId, onboardingId, taskId, cancellationToken);
        if (existing == null) return false;

        var cancelled = await _repo.CancelTaskAsync(actor.OrganizationId, onboardingId, taskId, cancellationToken);
        if (cancelled)
        {
            await _repo.AddActivityAsync(actor.OrganizationId, onboardingId, "TaskCancelled", $"Task cancelled: {existing.Title}",
                JsonSerializer.Serialize(new { taskId }), actor.UserId, actor.Name, cancellationToken);
        }
        return cancelled;
    }

    public async Task<OnboardingTaskDto?> CompleteTaskAsync(HrActor actor, Guid onboardingId, Guid taskId, CompleteOnboardingTaskRequest? request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OnboardingPermissions.ManageTasks);
        var header = await RequireHeaderAsync(actor.OrganizationId, onboardingId, cancellationToken);
        RequireActive(header);

        var tasks = await _repo.ListTasksAsync(actor.OrganizationId, onboardingId, cancellationToken);
        var task = tasks.FirstOrDefault(t => t.Id == taskId);
        if (task == null) return null;

        if (task.Status == OnboardingTaskStatuses.Cancelled)
            throw EmployeeServiceException.Conflict("Cancelled tasks cannot be completed.", "task_cancelled");
        if (task.IsWaived)
            throw EmployeeServiceException.Conflict("Waived tasks cannot be completed.", "task_waived");
        if (task.Status == OnboardingTaskStatuses.Completed)
            throw EmployeeServiceException.Conflict("The task is already completed.", "task_completed");
        if (request?.Notes is { Length: > 2000 })
            throw EmployeeServiceException.Validation("Notes must be 2000 characters or fewer.");

        var blocking = OnboardingValidation.FindUnmetDependency(task, tasks);
        if (blocking != null)
            throw EmployeeServiceException.Conflict($"Complete \"{blocking}\" first.", "dependency_incomplete");

        if (!await _repo.CompleteTaskAsync(actor.OrganizationId, onboardingId, taskId, request?.Notes, actor.UserId, cancellationToken))
            throw EmployeeServiceException.Conflict("The task changed in the meantime. Refresh and try again.", "task_conflict");

        await _repo.AddActivityAsync(actor.OrganizationId, onboardingId, "TaskCompleted",
            task.RequiresApproval ? $"Task completed, awaiting approval: {task.Title}" : $"Task completed: {task.Title}",
            JsonSerializer.Serialize(new { taskId, requiresApproval = task.RequiresApproval }), actor.UserId, actor.Name, cancellationToken);

        if (task.RequiresApproval)
            Notify(actor, onboardingId, OnboardingTriggers.TaskApprovalRequested, OnboardingNotificationKeys.ForEvent(OnboardingTriggers.TaskApprovalRequested, taskId), taskId);

        return await _repo.GetTaskAsync(actor.OrganizationId, onboardingId, taskId, cancellationToken);
    }

    public async Task<OnboardingTaskDto?> ReopenTaskAsync(HrActor actor, Guid onboardingId, Guid taskId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OnboardingPermissions.ManageTasks);
        var header = await RequireHeaderAsync(actor.OrganizationId, onboardingId, cancellationToken);
        RequireActive(header);

        var task = await _repo.GetTaskAsync(actor.OrganizationId, onboardingId, taskId, cancellationToken);
        if (task == null) return null;
        if (task.Status != OnboardingTaskStatuses.Completed)
            throw EmployeeServiceException.Conflict("Only completed tasks can be reopened.", "task_not_completed");

        if (!await _repo.ReopenTaskAsync(actor.OrganizationId, onboardingId, taskId, cancellationToken))
            throw EmployeeServiceException.Conflict("The task changed in the meantime. Refresh and try again.", "task_conflict");

        await _repo.AddActivityAsync(actor.OrganizationId, onboardingId, "TaskReopened", $"Task reopened: {task.Title}",
            JsonSerializer.Serialize(new { taskId }), actor.UserId, actor.Name, cancellationToken);

        await ReturnToProgressIfNoLongerCompleteAsync(actor, header, cancellationToken);
        return await _repo.GetTaskAsync(actor.OrganizationId, onboardingId, taskId, cancellationToken);
    }

    public async Task<OnboardingTaskDto?> ApproveTaskAsync(HrActor actor, Guid onboardingId, Guid taskId, ApproveOnboardingTaskRequest? request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OnboardingPermissions.ApproveTasks);
        var header = await RequireHeaderAsync(actor.OrganizationId, onboardingId, cancellationToken);
        RequireActive(header);

        var task = await _repo.GetTaskAsync(actor.OrganizationId, onboardingId, taskId, cancellationToken);
        if (task == null) return null;
        RequirePendingApproval(task);

        if (!await _repo.ApproveTaskAsync(actor.OrganizationId, onboardingId, taskId, actor.UserId, cancellationToken))
            throw EmployeeServiceException.Conflict("The task changed in the meantime. Refresh and try again.", "task_conflict");

        await _repo.AddActivityAsync(actor.OrganizationId, onboardingId, "TaskApproved", $"Task approved: {task.Title}",
            JsonSerializer.Serialize(new { taskId, notes = NullIfEmpty(request?.Notes) }), actor.UserId, actor.Name, cancellationToken);
        Notify(actor, onboardingId, OnboardingTriggers.TaskApproved, OnboardingNotificationKeys.ForEvent(OnboardingTriggers.TaskApproved, taskId), taskId);

        return await _repo.GetTaskAsync(actor.OrganizationId, onboardingId, taskId, cancellationToken);
    }

    public async Task<OnboardingTaskDto?> RejectTaskAsync(HrActor actor, Guid onboardingId, Guid taskId, RejectOnboardingTaskRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OnboardingPermissions.ApproveTasks);
        var header = await RequireHeaderAsync(actor.OrganizationId, onboardingId, cancellationToken);
        RequireActive(header);

        if (string.IsNullOrWhiteSpace(request.Reason))
            throw EmployeeServiceException.Validation("A rejection reason is required.", "reason_required");
        if (request.Reason.Length > 2000)
            throw EmployeeServiceException.Validation("The reason must be 2000 characters or fewer.");

        var task = await _repo.GetTaskAsync(actor.OrganizationId, onboardingId, taskId, cancellationToken);
        if (task == null) return null;
        RequirePendingApproval(task);

        if (!await _repo.RejectTaskAsync(actor.OrganizationId, onboardingId, taskId, request.Reason, actor.UserId, cancellationToken))
            throw EmployeeServiceException.Conflict("The task changed in the meantime. Refresh and try again.", "task_conflict");

        await _repo.AddActivityAsync(actor.OrganizationId, onboardingId, "TaskRejected", $"Task rejected: {task.Title}",
            JsonSerializer.Serialize(new { taskId, reason = request.Reason.Trim() }), actor.UserId, actor.Name, cancellationToken);
        Notify(actor, onboardingId, OnboardingTriggers.TaskRejected, OnboardingNotificationKeys.ForEvent(OnboardingTriggers.TaskRejected, taskId), taskId);

        await ReturnToProgressIfNoLongerCompleteAsync(actor, header, cancellationToken);
        return await _repo.GetTaskAsync(actor.OrganizationId, onboardingId, taskId, cancellationToken);
    }

    private static void RequirePendingApproval(OnboardingTaskDto task)
    {
        if (!task.RequiresApproval)
            throw EmployeeServiceException.Conflict("This task does not require approval.", "approval_not_required");
        if (task.Status != OnboardingTaskStatuses.Completed
            || !string.Equals(task.ApprovalStatus, OnboardingApprovalStatuses.Pending, StringComparison.OrdinalIgnoreCase))
            throw EmployeeServiceException.Conflict("Only completed tasks awaiting approval can be approved or rejected.", "approval_not_pending");
    }

    /// <summary>
    /// An onboarding in review must keep satisfying the completion gates. If a task change breaks them
    /// (reopened, rejected, newly added required task) the onboarding goes back to In Progress.
    /// </summary>
    private async Task ReturnToProgressIfNoLongerCompleteAsync(HrActor actor, OnboardingHeader header, CancellationToken cancellationToken)
    {
        if (!header.Status.Equals(OnboardingStatuses.InReview, StringComparison.OrdinalIgnoreCase)) return;

        var tasks = await _repo.ListTasksAsync(actor.OrganizationId, header.Id, cancellationToken);
        if (OnboardingValidation.CanComplete(tasks)) return;

        if (await _repo.TransitionStatusAsync(actor.OrganizationId, header.Id, OnboardingStatuses.InReview,
                OnboardingStatuses.InProgress, null, null, actor.UserId, cancellationToken))
        {
            await _repo.AddActivityAsync(actor.OrganizationId, header.Id, "StatusChanged",
                "Onboarding returned to In Progress because a required task is no longer complete",
                JsonSerializer.Serialize(new { from = OnboardingStatuses.InReview, to = OnboardingStatuses.InProgress }),
                actor.UserId, actor.Name, cancellationToken);
        }
    }

    // -------------------------------------------------------------------------
    // Documents
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<OnboardingDocumentDto>> ListDocumentsAsync(HrActor actor, Guid onboardingId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OnboardingPermissions.View);
        await RequireHeaderAsync(actor.OrganizationId, onboardingId, cancellationToken);
        return await _repo.ListDocumentsAsync(actor.OrganizationId, onboardingId, cancellationToken);
    }

    public async Task<OnboardingDocumentDto?> UpdateDocumentAsync(HrActor actor, Guid onboardingId, Guid documentId, UpdateOnboardingDocumentRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OnboardingPermissions.ManageDocuments);
        var header = await RequireHeaderAsync(actor.OrganizationId, onboardingId, cancellationToken);
        RequireActive(header);

        var status = OnboardingValidation.NormalizeDocumentStatus(request.Status)
                     ?? throw EmployeeServiceException.Validation(
                         $"Document status must be one of: {string.Join(", ", OnboardingDocumentStatuses.All.OrderBy(s => s))}.");

        var doc = await _repo.GetDocumentAsync(actor.OrganizationId, onboardingId, documentId, cancellationToken);
        if (doc == null) return null;

        if (!await _repo.UpdateDocumentAsync(actor.OrganizationId, onboardingId, documentId, status, request.EmployeeDocumentId, cancellationToken))
            return null;

        await _repo.AddActivityAsync(actor.OrganizationId, onboardingId, "DocumentUpdated", $"Document {doc.Name}: {status}",
            JsonSerializer.Serialize(new { documentId, from = doc.Status, to = status }), actor.UserId, actor.Name, cancellationToken);
        return await _repo.GetDocumentAsync(actor.OrganizationId, onboardingId, documentId, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Templates
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<OnboardingTemplateListItemDto>> ListTemplatesAsync(HrActor actor, OnboardingTemplateListQuery query, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OnboardingPermissions.View);
        await EnsureOrgReadyAsync(actor.OrganizationId, actor.UserId, cancellationToken);
        return await _repo.ListTemplatesAsync(actor.OrganizationId, query, cancellationToken);
    }

    public async Task<OnboardingTemplateDetailDto?> GetTemplateAsync(HrActor actor, Guid templateId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OnboardingPermissions.View);
        return await _repo.GetTemplateAsync(actor.OrganizationId, templateId, cancellationToken);
    }

    public async Task<OnboardingTemplateDetailDto> CreateTemplateAsync(HrActor actor, UpsertOnboardingTemplateRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OnboardingPermissions.ManageTemplates);
        await EnsureOrgReadyAsync(actor.OrganizationId, actor.UserId, cancellationToken);
        await ValidateTemplateAsync(actor, request, null, cancellationToken);

        var id = await _repo.CreateTemplateAsync(actor.OrganizationId, request, actor.UserId, cancellationToken);
        return await _repo.GetTemplateAsync(actor.OrganizationId, id, cancellationToken)
               ?? throw EmployeeServiceException.NotFound("Template was created but could not be loaded.");
    }

    public async Task<OnboardingTemplateDetailDto?> UpdateTemplateAsync(HrActor actor, Guid templateId, UpsertOnboardingTemplateRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OnboardingPermissions.ManageTemplates);

        var existing = await _repo.GetTemplateAsync(actor.OrganizationId, templateId, cancellationToken);
        if (existing == null) return null;
        if (existing.IsArchived)
            throw EmployeeServiceException.Conflict("Archived templates cannot be edited.", "template_archived");

        await ValidateTemplateAsync(actor, request, templateId, cancellationToken);

        // Existing onboardings keep their snapshot; only future onboardings use the new version.
        if (!await _repo.UpdateTemplateAsync(actor.OrganizationId, templateId, request, actor.UserId, cancellationToken)) return null;
        return await _repo.GetTemplateAsync(actor.OrganizationId, templateId, cancellationToken);
    }

    public async Task<OnboardingTemplateDetailDto?> DuplicateTemplateAsync(HrActor actor, Guid templateId, DuplicateOnboardingTemplateRequest? request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OnboardingPermissions.ManageTemplates);

        var source = await _repo.GetTemplateAsync(actor.OrganizationId, templateId, cancellationToken);
        if (source == null) return null;

        var baseName = string.IsNullOrWhiteSpace(request?.Name) ? $"{source.Name} (copy)" : request!.Name!.Trim();
        if (baseName.Length > 200) throw EmployeeServiceException.Validation("Template name must be 200 characters or fewer.");

        var name = baseName;
        for (var n = 2; await _repo.TemplateNameExistsAsync(actor.OrganizationId, name, null, cancellationToken); n++)
        {
            if (!string.IsNullOrWhiteSpace(request?.Name))
                throw EmployeeServiceException.Conflict($"A template named '{baseName}' already exists.", "template_name_exists");
            name = $"{baseName} {n}";
        }

        var id = await _repo.DuplicateTemplateAsync(actor.OrganizationId, templateId, name, actor.UserId, cancellationToken);
        return id == null ? null : await _repo.GetTemplateAsync(actor.OrganizationId, id.Value, cancellationToken);
    }

    public async Task<bool> ArchiveTemplateAsync(HrActor actor, Guid templateId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OnboardingPermissions.ManageTemplates);
        return await _repo.ArchiveTemplateAsync(actor.OrganizationId, templateId, actor.UserId, cancellationToken);
    }

    private async Task ValidateTemplateAsync(HrActor actor, UpsertOnboardingTemplateRequest request, Guid? excludeId, CancellationToken cancellationToken)
    {
        request.Tasks ??= new List<UpsertOnboardingTemplateTaskRequest>();
        var errors = OnboardingValidation.ValidateTemplate(request);
        if (errors.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", errors));

        request.Name = request.Name.Trim();
        if (await _repo.TemplateNameExistsAsync(actor.OrganizationId, request.Name, excludeId, cancellationToken))
            throw EmployeeServiceException.Conflict($"A template named '{request.Name}' already exists.", "template_name_exists");

        var referenceError = await _repo.CheckTemplateReferencesAsync(actor.OrganizationId, request, cancellationToken);
        if (referenceError != null) throw EmployeeServiceException.Validation(referenceError);

        foreach (var userId in request.Tasks.Select(t => t.DefaultAssigneeUserId).Where(id => id.HasValue).Select(id => id!.Value).Distinct())
            await EnsureUserInOrgAsync(actor.OrganizationId, userId, "Default assignee", cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Notifications
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<OnboardingNotificationSettingDto>> GetNotificationSettingsAsync(HrActor actor, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OnboardingPermissions.View);
        await EnsureOrgReadyAsync(actor.OrganizationId, actor.UserId, cancellationToken);
        return await _repo.GetNotificationSettingsAsync(actor.OrganizationId, cancellationToken);
    }

    public async Task<IReadOnlyList<OnboardingNotificationSettingDto>> UpdateNotificationSettingsAsync(
        HrActor actor, UpdateOnboardingNotificationSettingsRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OnboardingPermissions.ManageAutomations);
        await EnsureOrgReadyAsync(actor.OrganizationId, actor.UserId, cancellationToken);

        var errors = new List<string>();
        foreach (var setting in request.Settings ?? [])
        {
            var trigger = OnboardingTriggers.All.FirstOrDefault(t => t.Equals(setting.TriggerCode?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (trigger == null)
            {
                errors.Add($"Unknown trigger '{setting.TriggerCode}'.");
                continue;
            }
            setting.TriggerCode = trigger;

            if (setting.DaysBefore is < 0 or > 60) errors.Add($"{trigger}: days before must be between 0 and 60.");
            if (setting.Recipients != null)
            {
                var normalized = new List<string>();
                foreach (var r in setting.Recipients)
                {
                    var role = OnboardingValidation.NormalizeFromSet(OnboardingRecipientRoles.All, r);
                    if (role == null) errors.Add($"{trigger}: unknown recipient '{r}'.");
                    else if (!normalized.Contains(role)) normalized.Add(role);
                }
                setting.Recipients = normalized;
            }
        }
        if (errors.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", errors));

        foreach (var setting in request.Settings ?? [])
            await _repo.UpsertNotificationSettingAsync(actor.OrganizationId, setting, actor.UserId, cancellationToken);

        return await _repo.GetNotificationSettingsAsync(actor.OrganizationId, cancellationToken);
    }

    public async Task<IReadOnlyList<OnboardingNotificationDto>> ListNotificationsAsync(HrActor actor, Guid? onboardingId, int take, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OnboardingPermissions.ManageAutomations);
        return await _repo.ListNotificationsAsync(actor.OrganizationId, onboardingId, take <= 0 ? 50 : take, cancellationToken);
    }

    /// <summary>Fire-and-forget (deduplicated downstream); a notification problem must never fail the business operation.</summary>
    private void Notify(HrActor actor, Guid onboardingId, string trigger, string? dedupeKey = null, Guid? taskId = null)
    {
        try
        {
            _notifications.Enqueue(new OnboardingNotificationEvent(
                actor.OrganizationId, onboardingId, trigger,
                dedupeKey ?? OnboardingNotificationKeys.ForEvent(trigger, onboardingId), taskId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enqueue onboarding notification {Trigger}.", trigger);
        }
    }

    private async Task NotifyTaskAssignmentsAsync(HrActor actor, Guid onboardingId, CancellationToken cancellationToken)
    {
        try
        {
            var tasks = await _repo.ListTasksAsync(actor.OrganizationId, onboardingId, cancellationToken);
            foreach (var task in tasks.Where(t => t.AssignedUserId.HasValue
                                                  && t.Status != OnboardingTaskStatuses.Completed
                                                  && t.Status != OnboardingTaskStatuses.Cancelled
                                                  && !t.IsWaived))
            {
                Notify(actor, onboardingId, OnboardingTriggers.TaskAssigned,
                    OnboardingNotificationKeys.ForTask(OnboardingTriggers.TaskAssigned, task.Id, task.AssignedUserId), task.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not queue task assignment notifications for onboarding {OnboardingId}.", onboardingId);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task<OnboardingHeader> RequireHeaderAsync(Guid organizationId, Guid onboardingId, CancellationToken cancellationToken) =>
        await _repo.GetHeaderAsync(organizationId, onboardingId, cancellationToken)
        ?? throw EmployeeServiceException.NotFound("Onboarding not found.");

    private static void RequireActive(OnboardingHeader header)
    {
        if (OnboardingValidation.IsTerminalStatus(header.Status))
            throw EmployeeServiceException.Conflict($"A {header.Status} onboarding can no longer be modified.", "onboarding_closed");
    }

    private async Task EnsureUserInOrgAsync(Guid organizationId, Guid? userId, string label, CancellationToken cancellationToken)
    {
        if (userId.HasValue && !await _repo.OrgUserExistsAsync(organizationId, userId.Value, cancellationToken))
            throw EmployeeServiceException.Validation($"{label} user not found in this organization.");
    }

    private static bool IsUniqueViolation(SqlException ex) => ex.Number is 2601 or 2627;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static OnboardingTaskSpec ToSpec(CreateOnboardingTaskRequest r) => new()
    {
        Title = r.Title.Trim(),
        Description = r.Description,
        Category = OnboardingValidation.NormalizeCategory(r.Category) ?? OnboardingTaskCategories.Other,
        AssignmentType = OnboardingValidation.NormalizeAssignmentType(r.AssignmentType) ?? OnboardingAssignmentTypes.SpecificUser,
        AssignedUserId = r.AssignedUserId,
        DueDate = r.DueDate?.Date,
        IsRequired = r.IsRequired,
        RequiresDocument = r.RequiresDocument,
        DocumentCategory = r.DocumentCategory,
        RequiresApproval = r.RequiresApproval,
        DependsOnTaskId = r.DependsOnTaskId
    };

    private static OnboardingTaskSpec ToSpec(UpdateOnboardingTaskRequest r) => new()
    {
        Title = r.Title.Trim(),
        Description = r.Description,
        Category = OnboardingValidation.NormalizeCategory(r.Category) ?? OnboardingTaskCategories.Other,
        AssignmentType = OnboardingValidation.NormalizeAssignmentType(r.AssignmentType) ?? OnboardingAssignmentTypes.SpecificUser,
        AssignedUserId = r.AssignedUserId,
        DueDate = r.DueDate?.Date,
        IsRequired = r.IsRequired,
        RequiresDocument = r.RequiresDocument,
        DocumentCategory = r.DocumentCategory,
        RequiresApproval = r.RequiresApproval ?? false
    };
}
