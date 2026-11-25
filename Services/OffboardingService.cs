using System.Collections.Concurrent;
using System.Data.SqlClient;
using System.Text.Json;
using nexthire_api.DTOs;
using nexthire_api.DTOs.CoreHr;
using nexthire_api.Exceptions;
using nexthire_api.Repositories;
using nexthire_api.Security;

namespace nexthire_api.Services;

public class OffboardingService : IOffboardingService
{
    // Orgs whose offboarding defaults (templates, notification settings, dev demo data) this process already ensured.
    private static readonly ConcurrentDictionary<Guid, bool> OrgInitialized = new();

    // Throttle for the lazy "apply due terminations" check that piggybacks on list / pipeline / summary requests.
    private static readonly ConcurrentDictionary<Guid, DateTime> LastDueCheckUtc = new();
    private static readonly TimeSpan DueCheckInterval = TimeSpan.FromSeconds(60);

    private readonly IOffboardingRepository _repo;
    private readonly IEmployeeRepository _employees;
    private readonly IRoleRepository _roles;
    private readonly IOffboardingNotificationService _notifications;
    private readonly IHostEnvironment _env;
    private readonly ILogger<OffboardingService> _logger;

    public OffboardingService(
        IOffboardingRepository repo,
        IEmployeeRepository employees,
        IRoleRepository roles,
        IOffboardingNotificationService notifications,
        IHostEnvironment env,
        ILogger<OffboardingService> logger)
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
        if (!OffboardingPermissions.Has(codes, capability))
            throw new HrAccessDeniedException(capability);
        return codes;
    }

    public async Task<IReadOnlyList<string>> GetCapabilitiesAsync(HrActor actor, CancellationToken cancellationToken = default) =>
        OffboardingPermissions.ListFor(await RoleCodesAsync(actor));

    private async Task EnsureOrgReadyAsync(Guid organizationId, Guid? actorUserId, CancellationToken cancellationToken)
    {
        if (OrgInitialized.ContainsKey(organizationId)) return;

        // Demo offboardings need demo employees; both seeders are idempotent and development-only for demo data.
        await _employees.EnsureOrgDefaultsAsync(organizationId, cancellationToken);
        await _employees.SeedDemoEmployeesIfEmptyAsync(organizationId, actorUserId, _env.IsDevelopment(), cancellationToken);
        await _repo.EnsureOrgDefaultsAsync(organizationId, cancellationToken);
        await _repo.SeedDemoOffboardingsIfEmptyAsync(organizationId, actorUserId, _env.IsDevelopment(), cancellationToken);
        OrgInitialized[organizationId] = true;
    }

    // -------------------------------------------------------------------------
    // Employment status (separation -> employee record)
    // -------------------------------------------------------------------------

    public async Task<int> ApplyDueTerminationsAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        await _repo.ApplyDueTerminationsAsync(organizationId, null, "System", cancellationToken);

    /// <summary>
    /// Lightweight, best-effort check used by read endpoints: applies due terminations at most once per minute per org
    /// (per process) and never fails the caller's request.
    /// </summary>
    private async Task ApplyDueTerminationsSafelyAsync(Guid organizationId, bool force, CancellationToken cancellationToken)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (!force && LastDueCheckUtc.TryGetValue(organizationId, out var last) && now - last < DueCheckInterval) return;
            LastDueCheckUtc[organizationId] = now;
            await _repo.ApplyDueTerminationsAsync(organizationId, null, "System", cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Applying due terminations failed for organization {OrganizationId}.", organizationId);
        }
    }

    // -------------------------------------------------------------------------
    // Offboardings: read
    // -------------------------------------------------------------------------

    public async Task<PagedResult<OffboardingListItemDto>> ListAsync(HrActor actor, OffboardingListQuery query, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.View);
        await EnsureOrgReadyAsync(actor.OrganizationId, actor.UserId, cancellationToken);
        await ApplyDueTerminationsSafelyAsync(actor.OrganizationId, false, cancellationToken);

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            query.Status = OffboardingValidation.NormalizeStatus(query.Status)
                           ?? throw EmployeeServiceException.Validation(
                               $"Status filter must be one of: {string.Join(", ", OffboardingStatuses.All.OrderBy(s => s))}.");
        }
        else
        {
            query.Status = null;
        }
        query.SeparationType = NormalizeSeparationFilter(query.SeparationType);
        query.Page = Math.Max(1, query.Page);
        query.PageSize = query.PageSize <= 0 ? 25 : Math.Min(query.PageSize, 200);
        query.Dir = string.Equals(query.Dir, "desc", StringComparison.OrdinalIgnoreCase) ? "desc" : "asc";
        query.Sort = string.IsNullOrWhiteSpace(query.Sort) ? "lastWorkingDay" : query.Sort.Trim();
        return await _repo.ListAsync(actor.OrganizationId, query, cancellationToken);
    }

    public async Task<OffboardingPipelineDto> GetPipelineAsync(HrActor actor, OffboardingPipelineQuery query, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.View);
        await EnsureOrgReadyAsync(actor.OrganizationId, actor.UserId, cancellationToken);
        await ApplyDueTerminationsSafelyAsync(actor.OrganizationId, false, cancellationToken);
        query.SeparationType = NormalizeSeparationFilter(query.SeparationType);
        if (query.CompletedWithinDays <= 0) query.CompletedWithinDays = 60;
        return await _repo.PipelineAsync(actor.OrganizationId, query, cancellationToken);
    }

    public async Task<OffboardingSummaryDto> GetSummaryAsync(HrActor actor, DateTime? from, DateTime? to, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.View);
        await EnsureOrgReadyAsync(actor.OrganizationId, actor.UserId, cancellationToken);
        await ApplyDueTerminationsSafelyAsync(actor.OrganizationId, false, cancellationToken);
        return await _repo.GetSummaryAsync(actor.OrganizationId, from, to, cancellationToken);
    }

    public async Task<OffboardingDetailDto?> GetAsync(HrActor actor, Guid offboardingId, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, OffboardingPermissions.View);
        var detail = await _repo.GetAsync(actor.OrganizationId, offboardingId, cancellationToken);
        return detail == null ? null : Redact(detail, codes);
    }

    public async Task<IReadOnlyList<OffboardingActivityDto>> GetActivityAsync(HrActor actor, Guid offboardingId, int take, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.View);
        await RequireHeaderAsync(actor.OrganizationId, offboardingId, cancellationToken);
        return await _repo.ListActivityAsync(actor.OrganizationId, offboardingId, take <= 0 ? 50 : Math.Min(take, 500), cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Offboardings: create / update
    // -------------------------------------------------------------------------

    public async Task<OffboardingDetailDto> CreateAsync(HrActor actor, CreateOffboardingRequest request, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, OffboardingPermissions.Create);
        await EnsureOrgReadyAsync(actor.OrganizationId, actor.UserId, cancellationToken);

        // Strip operations the caller is not allowed to perform instead of failing the whole request.
        if (!OffboardingPermissions.Has(codes, OffboardingPermissions.ManageTasks))
        {
            request.AdditionalTasks = null;
            request.ExcludedTemplateTaskIds = null;
        }
        if (!OffboardingPermissions.Has(codes, OffboardingPermissions.ManageAssets)) request.Assets = null;
        if (!OffboardingPermissions.Has(codes, OffboardingPermissions.ManageAccess)) request.AccessSystems = null;
        var canConfidential = OffboardingPermissions.Has(codes, OffboardingPermissions.ViewConfidentialInformation);
        if (!canConfidential)
        {
            request.SeparationReason = null;
            request.ConfidentialNotes = null;
        }

        var errors = OffboardingValidation.ValidateCreateOffboarding(request);
        if (errors.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", errors));
        var separationType = OffboardingValidation.NormalizeSeparationType(request.SeparationType)!;

        var employee = await _repo.GetEmployeeInfoAsync(actor.OrganizationId, request.EmployeeId, cancellationToken)
                       ?? throw EmployeeServiceException.NotFound("Employee not found.");
        if (employee.IsArchived)
            throw EmployeeServiceException.Conflict("Archived employees cannot be offboarded.", "employee_archived");
        if (employee.EmploymentStatus.Equals(EmploymentStatuses.Terminated, StringComparison.OrdinalIgnoreCase))
            throw EmployeeServiceException.Conflict("This employee is already terminated.", "employee_terminated");
        if (await _repo.FindActiveOffboardingIdAsync(actor.OrganizationId, request.EmployeeId, cancellationToken) != null)
            throw EmployeeServiceException.Conflict("This employee already has an active offboarding.", "active_offboarding_exists");

        if (request.LastWorkingDay.HasValue && employee.HireDate.HasValue
            && request.LastWorkingDay.Value.Date < employee.HireDate.Value.Date)
            throw EmployeeServiceException.Validation("The last working day cannot be before the employee's hire date.");

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
            templateId = await _repo.ResolveTemplateIdForEmployeeAsync(actor.OrganizationId, request.EmployeeId, separationType, cancellationToken);
        }

        var command = new CreateOffboardingCommand
        {
            EmployeeId = request.EmployeeId,
            TemplateId = templateId,
            OwnerUserId = request.OwnerUserId,
            SeparationType = separationType,
            SeparationReason = NullIfEmpty(request.SeparationReason),
            NoticeDate = request.NoticeDate?.Date,
            LastWorkingDay = request.LastWorkingDay?.Date,
            TerminationEffectiveDate = OffboardingValidation.ResolveTerminationEffectiveDate(request.TerminationEffectiveDate, request.LastWorkingDay),
            ExpectedCompletionDate = request.ExpectedCompletionDate?.Date,
            RehireEligibility = OffboardingValidation.NormalizeRehireEligibility(request.RehireEligibility),
            ConfidentialNotes = NullIfEmpty(request.ConfidentialNotes),
            // Explicit opt-in only, and never for involuntary separations (validated above, enforced again here).
            NotifyEmployee = OffboardingValidation.CanNotifyEmployee(separationType, request.NotifyEmployee),
            InitialStatus = request.StartImmediately ? OffboardingStatuses.InProgress : OffboardingStatuses.Scheduled,
            ExcludedTemplateTaskIds = request.ExcludedTemplateTaskIds ?? [],
            AdditionalTasks = (request.AdditionalTasks ?? []).Select(ToSpec).ToList(),
            Assets = (request.Assets ?? []).Select(a => new OffboardingAssetSpec
            {
                AssetName = a.AssetName.Trim(),
                AssetType = NullIfEmpty(a.AssetType),
                SerialNumber = NullIfEmpty(a.SerialNumber),
                ReturnRequired = a.ReturnRequired,
                ExpectedReturnDate = a.ExpectedReturnDate?.Date,
                Notes = NullIfEmpty(a.Notes)
            }).ToList(),
            AccessSystems = request.AccessSystems?.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ActorUserId = actor.UserId,
            ActorName = actor.Name
        };

        CreatedOffboarding created;
        try
        {
            created = await _repo.CreateOffboardingAsync(actor.OrganizationId, command, cancellationToken);
        }
        catch (SqlException ex) when (IsUniqueViolation(ex))
        {
            throw EmployeeServiceException.Conflict("This employee already has an active offboarding.", "active_offboarding_exists");
        }

        await _repo.LinkAtsTasksAsync(actor.OrganizationId, created.Id, cancellationToken);

        Notify(actor, created.Id, OffboardingTriggers.OffboardingCreated);
        if (request.StartImmediately)
        {
            Notify(actor, created.Id, OffboardingTriggers.OffboardingStarted);
            await NotifyTaskAssignmentsAsync(actor, created.Id, cancellationToken);
            await ApplyDueTerminationsSafelyAsync(actor.OrganizationId, true, cancellationToken);
        }

        var detail = await _repo.GetAsync(actor.OrganizationId, created.Id, cancellationToken)
                     ?? throw EmployeeServiceException.NotFound("Offboarding was created but could not be loaded.");
        return Redact(detail, codes);
    }

    public async Task<OffboardingDetailDto> StartOffboardingForEmployeeAsync(
        HrActor actor, Guid employeeId, StartOffboardingForEmployeeRequest? request, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, OffboardingPermissions.Create);
        await EnsureOrgReadyAsync(actor.OrganizationId, actor.UserId, cancellationToken);

        var existingId = await _repo.FindActiveOffboardingIdAsync(actor.OrganizationId, employeeId, cancellationToken);
        if (existingId.HasValue)
        {
            var existing = await _repo.GetAsync(actor.OrganizationId, existingId.Value, cancellationToken)
                           ?? throw EmployeeServiceException.NotFound("Offboarding not found.");
            return Redact(existing, codes);
        }

        request ??= new StartOffboardingForEmployeeRequest();
        var errors = OffboardingValidation.ValidateStartForEmployee(request);
        if (errors.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", errors));

        try
        {
            return await CreateAsync(actor, new CreateOffboardingRequest
            {
                EmployeeId = employeeId,
                SeparationType = request.SeparationType!,
                SeparationReason = request.SeparationReason,
                NoticeDate = request.NoticeDate,
                LastWorkingDay = request.LastWorkingDay,
                TerminationEffectiveDate = request.TerminationEffectiveDate,
                TemplateId = request.TemplateId,
                OwnerUserId = request.OwnerUserId,
                NotifyEmployee = request.NotifyEmployee,
                StartImmediately = request.StartImmediately
            }, cancellationToken);
        }
        catch (EmployeeServiceException ex) when (ex.Code == "active_offboarding_exists")
        {
            // Lost a race with a concurrent call: stay idempotent and return the winner.
            var id = await _repo.FindActiveOffboardingIdAsync(actor.OrganizationId, employeeId, cancellationToken);
            if (id.HasValue)
            {
                var detail = await _repo.GetAsync(actor.OrganizationId, id.Value, cancellationToken);
                if (detail != null) return Redact(detail, codes);
            }
            throw;
        }
    }

    public async Task<OffboardingDetailDto?> UpdateAsync(HrActor actor, Guid offboardingId, UpdateOffboardingRequest request, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, OffboardingPermissions.Edit);

        var header = await _repo.GetHeaderAsync(actor.OrganizationId, offboardingId, cancellationToken);
        if (header == null) return null;
        RequireActive(header);

        var canConfidential = OffboardingPermissions.Has(codes, OffboardingPermissions.ViewConfidentialInformation);
        if (!canConfidential)
        {
            request.SeparationReason = null;
            request.ConfidentialNotes = null;
        }

        var errors = OffboardingValidation.ValidateUpdateOffboarding(request);
        if (errors.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", errors));

        var separationType = OffboardingValidation.NormalizeSeparationType(request.SeparationType) ?? header.SeparationType;

        // Explicit notify flag: null keeps the current value; involuntary separations can never notify the employee.
        bool notify;
        if (request.NotifyEmployee.HasValue)
        {
            var notifyError = OffboardingValidation.ValidateNotifyEmployee(separationType, request.NotifyEmployee.Value);
            if (notifyError != null) throw EmployeeServiceException.Validation(notifyError);
            notify = request.NotifyEmployee.Value;
        }
        else
        {
            notify = OffboardingValidation.CanNotifyEmployee(separationType, header.NotifyEmployee);
        }

        // PUT is a full overwrite, but an omitted effective date keeps an explicitly chosen one and otherwise follows the last working day.
        var lastWorkingDay = request.LastWorkingDay?.Date;
        DateTime? effective = request.TerminationEffectiveDate?.Date;
        if (effective == null)
        {
            var previousExplicit = header.TerminationEffectiveDate.HasValue
                                   && header.TerminationEffectiveDate.Value.Date != header.LastWorkingDay?.Date;
            effective = previousExplicit ? header.TerminationEffectiveDate!.Value.Date : lastWorkingDay ?? header.TerminationEffectiveDate?.Date;
        }
        var dateErrors = OffboardingValidation.ValidateSeparationDates(request.NoticeDate, lastWorkingDay, effective);
        if (dateErrors.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", dateErrors));

        // Once the employee record was flipped to Terminated the separation facts it was based on are frozen.
        if (header.EmploymentStatusApplied)
        {
            if (!separationType.Equals(header.SeparationType, StringComparison.OrdinalIgnoreCase)
                || effective?.Date != header.TerminationEffectiveDate?.Date)
                throw EmployeeServiceException.Conflict(
                    "The employee has already been marked as terminated; the separation type and termination effective date can no longer be changed.",
                    "employment_status_applied");
        }

        var employee = await _repo.GetEmployeeInfoAsync(actor.OrganizationId, header.EmployeeId, cancellationToken);
        if (lastWorkingDay.HasValue && employee?.HireDate.HasValue == true && lastWorkingDay.Value < employee.HireDate!.Value.Date)
            throw EmployeeServiceException.Validation("The last working day cannot be before the employee's hire date.");

        await EnsureUserInOrgAsync(actor.OrganizationId, request.OwnerUserId, "Owner", cancellationToken);

        var command = new UpdateOffboardingCommand
        {
            OwnerUserId = request.OwnerUserId,
            SeparationType = separationType,
            SeparationReason = NullIfEmpty(request.SeparationReason),
            NoticeDate = request.NoticeDate?.Date,
            LastWorkingDay = lastWorkingDay,
            TerminationEffectiveDate = effective,
            ExpectedCompletionDate = request.ExpectedCompletionDate?.Date,
            RehireEligibility = OffboardingValidation.NormalizeRehireEligibility(request.RehireEligibility),
            ConfidentialNotes = NullIfEmpty(request.ConfidentialNotes),
            NotifyEmployee = notify,
            UpdateConfidential = canConfidential
        };
        if (!await _repo.UpdateAsync(actor.OrganizationId, offboardingId, command, actor.UserId, cancellationToken)) return null;

        var changes = new List<string>();
        if (header.OwnerUserId != request.OwnerUserId) changes.Add("owner");
        if (!header.SeparationType.Equals(separationType, StringComparison.OrdinalIgnoreCase)) changes.Add("separation type");
        if (header.NoticeDate?.Date != request.NoticeDate?.Date) changes.Add("notice date");
        if (header.LastWorkingDay?.Date != lastWorkingDay) changes.Add("last working day");
        if (header.TerminationEffectiveDate?.Date != effective) changes.Add("termination effective date");
        if (header.ExpectedCompletionDate?.Date != request.ExpectedCompletionDate?.Date) changes.Add("expected completion date");
        if (header.NotifyEmployee != notify) changes.Add("employee notification");
        if (canConfidential) changes.Add("confidential details"); // field names only; contents are never written to the activity log
        await _repo.AddActivityAsync(actor.OrganizationId, offboardingId, "Updated", "Offboarding details updated",
            changes.Count > 0 ? JsonSerializer.Serialize(new { changed = changes }) : null, actor.UserId, actor.Name, cancellationToken);

        // A date change can make the termination due right now.
        await ApplyDueTerminationsSafelyAsync(actor.OrganizationId, true, cancellationToken);

        return await LoadDetailAsync(actor, codes, offboardingId, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Status transitions
    // -------------------------------------------------------------------------

    public async Task<OffboardingDetailDto?> StartAsync(HrActor actor, Guid offboardingId, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, OffboardingPermissions.Edit);
        return await ChangeStatusCoreAsync(actor, codes, offboardingId, OffboardingStatuses.InProgress, null, null, null, cancellationToken);
    }

    public async Task<OffboardingDetailDto?> SubmitClearanceAsync(HrActor actor, Guid offboardingId, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, OffboardingPermissions.Edit);
        return await ChangeStatusCoreAsync(actor, codes, offboardingId, OffboardingStatuses.PendingClearance, null, null, null, cancellationToken);
    }

    public async Task<OffboardingDetailDto?> ReturnToProgressAsync(HrActor actor, Guid offboardingId, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, OffboardingPermissions.Edit);
        var header = await _repo.GetHeaderAsync(actor.OrganizationId, offboardingId, cancellationToken);
        if (header == null) return null;
        if (!header.Status.Equals(OffboardingStatuses.PendingClearance, StringComparison.OrdinalIgnoreCase))
            throw EmployeeServiceException.Conflict("Only an offboarding pending clearance can be returned to In Progress.", "invalid_transition");
        return await ChangeStatusCoreAsync(actor, codes, offboardingId, OffboardingStatuses.InProgress, null, null, null, cancellationToken);
    }

    public async Task<OffboardingDetailDto?> CompleteAsync(HrActor actor, Guid offboardingId, CompleteOffboardingRequest? request, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, OffboardingPermissions.Complete);
        return await ChangeStatusCoreAsync(actor, codes, offboardingId, OffboardingStatuses.Completed, null,
            request?.Notes, request?.CompletionDate, cancellationToken);
    }

    public async Task<OffboardingDetailDto?> CancelAsync(HrActor actor, Guid offboardingId, CancelOffboardingRequest request, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, OffboardingPermissions.Cancel);
        return await ChangeStatusCoreAsync(actor, codes, offboardingId, OffboardingStatuses.Cancelled, request.Reason, null, null, cancellationToken);
    }

    public async Task<OffboardingDetailDto?> ChangeStatusAsync(HrActor actor, Guid offboardingId, ChangeOffboardingStatusRequest request, CancellationToken cancellationToken = default)
    {
        var target = OffboardingValidation.NormalizeStatus(request.Status)
                     ?? throw EmployeeServiceException.Validation(
                         $"Status must be one of: {string.Join(", ", OffboardingStatuses.All.OrderBy(s => s))}.");

        // Each destination requires its own capability so dragging cards cannot bypass Complete / Cancel permissions.
        var capability = target switch
        {
            OffboardingStatuses.Completed => OffboardingPermissions.Complete,
            OffboardingStatuses.Cancelled => OffboardingPermissions.Cancel,
            _ => OffboardingPermissions.Edit
        };
        var codes = await RequireAsync(actor, capability);

        return await ChangeStatusCoreAsync(actor, codes, offboardingId, target, request.Reason, request.Notes, null, cancellationToken);
    }

    private async Task<OffboardingDetailDto?> ChangeStatusCoreAsync(
        HrActor actor, IReadOnlyList<string> codes, Guid offboardingId, string target, string? reason, string? notes,
        DateTime? completionDate, CancellationToken cancellationToken)
    {
        var header = await _repo.GetHeaderAsync(actor.OrganizationId, offboardingId, cancellationToken);
        if (header == null) return null;
        var from = header.Status;

        if (from.Equals(target, StringComparison.OrdinalIgnoreCase))
            throw EmployeeServiceException.Conflict($"The offboarding is already {target}.", "invalid_transition");
        if (!OffboardingValidation.CanTransition(from, target))
        {
            throw EmployeeServiceException.Conflict(
                OffboardingValidation.IsTerminalStatus(from)
                    ? $"A {from} offboarding cannot change status."
                    : $"An offboarding cannot move from {from} to {target}.",
                "invalid_transition");
        }
        if (target == OffboardingStatuses.Cancelled && string.IsNullOrWhiteSpace(reason))
            throw EmployeeServiceException.Validation("A cancellation reason is required.", "reason_required");
        if (reason is { Length: > 1000 })
            throw EmployeeServiceException.Validation("The reason must be 1000 characters or fewer.");
        if (notes is { Length: > 2000 })
            throw EmployeeServiceException.Validation("Notes must be 2000 characters or fewer.");

        var today = DateTime.UtcNow.Date;
        DateTime? completion = null;
        if (target == OffboardingStatuses.Completed)
        {
            completion = (completionDate ?? today).Date;
            if (completion > today)
                throw EmployeeServiceException.Validation("The completion date cannot be in the future.");
            if (header.ProcessStartDate.HasValue && completion < header.ProcessStartDate.Value.Date)
                throw EmployeeServiceException.Validation("The completion date cannot be before the process start date.");
        }

        // Single source of truth for every gate (PendingClearance, Completed and drag-to-Completed all land here).
        var tasks = await _repo.ListTasksAsync(actor.OrganizationId, offboardingId, cancellationToken);
        IReadOnlyList<OffboardingAssetDto>? assets = null;
        IReadOnlyList<OffboardingAccessItemDto>? accessItems = null;
        IReadOnlyList<OffboardingSettlementItemDto>? settlement = null;
        if (target == OffboardingStatuses.Completed)
        {
            assets = await _repo.ListAssetsAsync(actor.OrganizationId, offboardingId, cancellationToken);
            accessItems = await _repo.ListAccessItemsAsync(actor.OrganizationId, offboardingId, cancellationToken);
            settlement = await _repo.ListSettlementItemsAsync(actor.OrganizationId, offboardingId, cancellationToken);
        }

        var error = OffboardingValidation.ValidateTransition(from, target, tasks, reason, assets, accessItems, settlement);
        if (error != null)
        {
            throw EmployeeServiceException.Conflict(error,
                target == OffboardingStatuses.Completed ? "completion_blocked" : "required_tasks_incomplete");
        }

        var applied = await _repo.TransitionStatusAsync(
            actor.OrganizationId, offboardingId, from, target, completion, reason, actor.UserId, cancellationToken);
        if (!applied)
            throw EmployeeServiceException.Conflict("The offboarding status changed in the meantime. Refresh and try again.", "status_conflict");

        var summary = target switch
        {
            OffboardingStatuses.InProgress when from == OffboardingStatuses.Scheduled => "Offboarding started",
            OffboardingStatuses.InProgress => "Offboarding returned to In Progress",
            OffboardingStatuses.PendingClearance => "Offboarding submitted for clearance",
            OffboardingStatuses.Completed => "Offboarding completed",
            OffboardingStatuses.Cancelled => "Offboarding cancelled",
            _ => $"Status changed from {from} to {target}"
        };
        // Cancelling never restores the employee: make that visible in the audit trail when the status was already applied.
        object details = target == OffboardingStatuses.Cancelled && header.EmploymentStatusApplied
            ? new { from, to = target, reason = NullIfEmpty(reason), notes = NullIfEmpty(notes), employmentStatusApplied = true, employmentStatusRestored = false }
            : new { from, to = target, reason = NullIfEmpty(reason), notes = NullIfEmpty(notes), employmentStatusApplied = (bool?)null, employmentStatusRestored = (bool?)null };
        await _repo.AddActivityAsync(actor.OrganizationId, offboardingId, "StatusChanged", summary,
            JsonSerializer.Serialize(details, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }),
            actor.UserId, actor.Name, cancellationToken);

        switch (target)
        {
            case OffboardingStatuses.InProgress when from == OffboardingStatuses.Scheduled:
                Notify(actor, offboardingId, OffboardingTriggers.OffboardingStarted);
                await NotifyTaskAssignmentsAsync(actor, offboardingId, cancellationToken);
                break;
            case OffboardingStatuses.PendingClearance:
                Notify(actor, offboardingId, OffboardingTriggers.OffboardingPendingClearance);
                break;
            case OffboardingStatuses.Completed:
                Notify(actor, offboardingId, OffboardingTriggers.OffboardingCompleted);
                break;
            case OffboardingStatuses.Cancelled:
                Notify(actor, offboardingId, OffboardingTriggers.OffboardingCancelled);
                break;
        }

        if (target == OffboardingStatuses.InProgress && from == OffboardingStatuses.Scheduled)
        {
            await _repo.LinkAtsTasksAsync(actor.OrganizationId, offboardingId, cancellationToken);
            await ApplyDueTerminationsSafelyAsync(actor.OrganizationId, true, cancellationToken);
        }
        else if (target == OffboardingStatuses.Completed)
        {
            // Completion applies this offboarding's termination when its effective date has arrived; a future date is
            // picked up later by ApplyDueTerminations. Failures here must not undo the completion.
            try
            {
                await _repo.ApplyEmploymentStatusAsync(actor.OrganizationId, offboardingId, actor.UserId, actor.Name, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Applying employment status failed for offboarding {OffboardingId}; it will be retried on the next check.", offboardingId);
            }
        }

        return await LoadDetailAsync(actor, codes, offboardingId, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Tasks
    // -------------------------------------------------------------------------

    public async Task<OffboardingTaskDto> AddTaskAsync(HrActor actor, Guid offboardingId, CreateOffboardingTaskRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.ManageTasks);
        var header = await RequireHeaderAsync(actor.OrganizationId, offboardingId, cancellationToken);
        RequireActive(header);

        var errors = OffboardingValidation.ValidateCustomTask(request);
        if (errors.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", errors));
        await EnsureUserInOrgAsync(actor.OrganizationId, request.AssignedUserId, "Assignee", cancellationToken);

        if (request.DependsOnTaskId.HasValue
            && await _repo.GetTaskAsync(actor.OrganizationId, offboardingId, request.DependsOnTaskId.Value, cancellationToken) == null)
            throw EmployeeServiceException.Validation("The task this one depends on was not found in this offboarding.");

        var taskId = await _repo.AddTaskAsync(actor.OrganizationId, offboardingId, ToSpec(request), cancellationToken);
        await _repo.AddActivityAsync(actor.OrganizationId, offboardingId, "TaskAdded", $"Task added: {request.Title.Trim()}",
            JsonSerializer.Serialize(new { taskId }), actor.UserId, actor.Name, cancellationToken);

        await _repo.LinkAtsTasksAsync(actor.OrganizationId, offboardingId, cancellationToken);
        if (request.AssignedUserId.HasValue && header.Status == OffboardingStatuses.InProgress)
            Notify(actor, offboardingId, OffboardingTriggers.TaskAssigned, OffboardingNotificationKeys.ForTask(OffboardingTriggers.TaskAssigned, taskId, request.AssignedUserId), taskId);

        await ReturnToProgressIfNoLongerClearedAsync(actor, header, cancellationToken);

        return await _repo.GetTaskAsync(actor.OrganizationId, offboardingId, taskId, cancellationToken)
               ?? throw EmployeeServiceException.NotFound("Task was created but could not be loaded.");
    }

    public async Task<OffboardingTaskDto?> UpdateTaskAsync(HrActor actor, Guid offboardingId, Guid taskId, UpdateOffboardingTaskRequest request, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, OffboardingPermissions.ManageTasks);
        var header = await RequireHeaderAsync(actor.OrganizationId, offboardingId, cancellationToken);
        RequireActive(header);

        var existing = await _repo.GetTaskAsync(actor.OrganizationId, offboardingId, taskId, cancellationToken);
        if (existing == null) return null;
        if (existing.Status == OffboardingTaskStatuses.Cancelled)
            throw EmployeeServiceException.Conflict("Cancelled tasks cannot be edited.", "task_cancelled");

        // Waiving a task or toggling its approval requirement can bypass clearance: approvers only.
        var canApprove = OffboardingPermissions.Has(codes, OffboardingPermissions.ApproveClearance);
        if (!canApprove)
        {
            request.IsWaived = null;
            request.WaiveReason = null;
        }

        var errors = OffboardingValidation.ValidateTaskUpdate(request);
        if (errors.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", errors));
        await EnsureUserInOrgAsync(actor.OrganizationId, request.AssignedUserId, "Assignee", cancellationToken);

        if (request.IsWaived == true && existing.Status == OffboardingTaskStatuses.Completed)
            throw EmployeeServiceException.Conflict("Completed tasks cannot be waived. Reopen the task first.", "task_completed");

        var requiresApproval = canApprove && request.RequiresApproval.HasValue ? request.RequiresApproval.Value : existing.RequiresApproval;
        var isWaived = canApprove ? request.IsWaived : null;

        var spec = ToSpec(request);
        spec.RequiresApproval = requiresApproval;
        spec.DependsOnTaskId = existing.DependsOnTaskId;
        var status = OffboardingValidation.NormalizeFromSet(OffboardingTaskStatuses.Settable, request.Status);

        if (!await _repo.UpdateTaskAsync(actor.OrganizationId, offboardingId, taskId, spec, status, isWaived,
                isWaived == true ? request.WaiveReason?.Trim() : null, cancellationToken))
            return null;

        await _repo.AddActivityAsync(actor.OrganizationId, offboardingId, "TaskUpdated", $"Task updated: {spec.Title.Trim()}",
            JsonSerializer.Serialize(new { taskId, waived = isWaived }), actor.UserId, actor.Name, cancellationToken);

        await _repo.LinkAtsTasksAsync(actor.OrganizationId, offboardingId, cancellationToken);
        if (request.AssignedUserId.HasValue && request.AssignedUserId != existing.AssignedUserId && header.Status == OffboardingStatuses.InProgress)
            Notify(actor, offboardingId, OffboardingTriggers.TaskAssigned, OffboardingNotificationKeys.ForTask(OffboardingTriggers.TaskAssigned, taskId, request.AssignedUserId), taskId);

        await ReturnToProgressIfNoLongerClearedAsync(actor, header, cancellationToken);
        return await _repo.GetTaskAsync(actor.OrganizationId, offboardingId, taskId, cancellationToken);
    }

    public async Task<bool> CancelTaskAsync(HrActor actor, Guid offboardingId, Guid taskId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.ManageTasks);
        var header = await RequireHeaderAsync(actor.OrganizationId, offboardingId, cancellationToken);
        RequireActive(header);

        var existing = await _repo.GetTaskAsync(actor.OrganizationId, offboardingId, taskId, cancellationToken);
        if (existing == null) return false;

        var cancelled = await _repo.CancelTaskAsync(actor.OrganizationId, offboardingId, taskId, cancellationToken);
        if (cancelled)
        {
            await _repo.AddActivityAsync(actor.OrganizationId, offboardingId, "TaskCancelled", $"Task cancelled: {existing.Title}",
                JsonSerializer.Serialize(new { taskId }), actor.UserId, actor.Name, cancellationToken);
        }
        return cancelled;
    }

    public async Task<OffboardingTaskDto?> CompleteTaskAsync(HrActor actor, Guid offboardingId, Guid taskId, CompleteOffboardingTaskRequest? request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.ManageTasks);
        var header = await RequireHeaderAsync(actor.OrganizationId, offboardingId, cancellationToken);
        RequireActive(header);

        var tasks = await _repo.ListTasksAsync(actor.OrganizationId, offboardingId, cancellationToken);
        var task = tasks.FirstOrDefault(t => t.Id == taskId);
        if (task == null) return null;

        if (task.Status == OffboardingTaskStatuses.Cancelled)
            throw EmployeeServiceException.Conflict("Cancelled tasks cannot be completed.", "task_cancelled");
        if (task.IsWaived)
            throw EmployeeServiceException.Conflict("Waived tasks cannot be completed.", "task_waived");
        if (task.Status == OffboardingTaskStatuses.Completed)
            throw EmployeeServiceException.Conflict("The task is already completed.", "task_completed");
        if (request?.Notes is { Length: > 2000 })
            throw EmployeeServiceException.Validation("Notes must be 2000 characters or fewer.");
        if (request?.EvidenceUrl is { Length: > 1000 })
            throw EmployeeServiceException.Validation("Evidence URL must be 1000 characters or fewer.");

        var blocking = OffboardingValidation.FindUnmetDependency(task, tasks);
        if (blocking != null)
            throw EmployeeServiceException.Conflict($"Complete \"{blocking}\" first.", "dependency_incomplete");

        if (!await _repo.CompleteTaskAsync(actor.OrganizationId, offboardingId, taskId, NullIfEmpty(request?.Notes), NullIfEmpty(request?.EvidenceUrl), actor.UserId, cancellationToken))
            throw EmployeeServiceException.Conflict("The task changed in the meantime. Refresh and try again.", "task_conflict");

        await _repo.AddActivityAsync(actor.OrganizationId, offboardingId, "TaskCompleted",
            task.RequiresApproval ? $"Task completed, awaiting approval: {task.Title}" : $"Task completed: {task.Title}",
            JsonSerializer.Serialize(new { taskId, requiresApproval = task.RequiresApproval }), actor.UserId, actor.Name, cancellationToken);

        if (task.RequiresApproval)
            Notify(actor, offboardingId, OffboardingTriggers.TaskApprovalRequested, OffboardingNotificationKeys.ForEvent(OffboardingTriggers.TaskApprovalRequested, taskId), taskId);

        return await _repo.GetTaskAsync(actor.OrganizationId, offboardingId, taskId, cancellationToken);
    }

    public async Task<OffboardingTaskDto?> ReopenTaskAsync(HrActor actor, Guid offboardingId, Guid taskId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.ManageTasks);
        var header = await RequireHeaderAsync(actor.OrganizationId, offboardingId, cancellationToken);
        RequireActive(header);

        var task = await _repo.GetTaskAsync(actor.OrganizationId, offboardingId, taskId, cancellationToken);
        if (task == null) return null;
        if (task.Status != OffboardingTaskStatuses.Completed)
            throw EmployeeServiceException.Conflict("Only completed tasks can be reopened.", "task_not_completed");

        if (!await _repo.ReopenTaskAsync(actor.OrganizationId, offboardingId, taskId, cancellationToken))
            throw EmployeeServiceException.Conflict("The task changed in the meantime. Refresh and try again.", "task_conflict");

        await _repo.AddActivityAsync(actor.OrganizationId, offboardingId, "TaskReopened", $"Task reopened: {task.Title}",
            JsonSerializer.Serialize(new { taskId }), actor.UserId, actor.Name, cancellationToken);

        await ReturnToProgressIfNoLongerClearedAsync(actor, header, cancellationToken);
        return await _repo.GetTaskAsync(actor.OrganizationId, offboardingId, taskId, cancellationToken);
    }

    public async Task<OffboardingTaskDto?> ApproveTaskAsync(HrActor actor, Guid offboardingId, Guid taskId, ApproveOffboardingTaskRequest? request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.ApproveClearance);
        var header = await RequireHeaderAsync(actor.OrganizationId, offboardingId, cancellationToken);
        RequireActive(header);

        if (request?.Notes is { Length: > 2000 })
            throw EmployeeServiceException.Validation("Notes must be 2000 characters or fewer.");

        var task = await _repo.GetTaskAsync(actor.OrganizationId, offboardingId, taskId, cancellationToken);
        if (task == null) return null;
        RequirePendingApproval(task);

        if (!await _repo.ApproveTaskAsync(actor.OrganizationId, offboardingId, taskId, actor.UserId, cancellationToken))
            throw EmployeeServiceException.Conflict("The task changed in the meantime. Refresh and try again.", "task_conflict");

        await _repo.AddActivityAsync(actor.OrganizationId, offboardingId, "TaskApproved", $"Task approved: {task.Title}",
            JsonSerializer.Serialize(new { taskId, notes = NullIfEmpty(request?.Notes) }), actor.UserId, actor.Name, cancellationToken);
        Notify(actor, offboardingId, OffboardingTriggers.TaskApproved, OffboardingNotificationKeys.ForEvent(OffboardingTriggers.TaskApproved, taskId), taskId);

        return await _repo.GetTaskAsync(actor.OrganizationId, offboardingId, taskId, cancellationToken);
    }

    public async Task<OffboardingTaskDto?> RejectTaskAsync(HrActor actor, Guid offboardingId, Guid taskId, RejectOffboardingTaskRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.ApproveClearance);
        var header = await RequireHeaderAsync(actor.OrganizationId, offboardingId, cancellationToken);
        RequireActive(header);

        if (string.IsNullOrWhiteSpace(request.Reason))
            throw EmployeeServiceException.Validation("A rejection reason is required.", "reason_required");
        if (request.Reason.Length > 2000)
            throw EmployeeServiceException.Validation("The reason must be 2000 characters or fewer.");

        var task = await _repo.GetTaskAsync(actor.OrganizationId, offboardingId, taskId, cancellationToken);
        if (task == null) return null;
        RequirePendingApproval(task);

        if (!await _repo.RejectTaskAsync(actor.OrganizationId, offboardingId, taskId, request.Reason.Trim(), actor.UserId, cancellationToken))
            throw EmployeeServiceException.Conflict("The task changed in the meantime. Refresh and try again.", "task_conflict");

        await _repo.AddActivityAsync(actor.OrganizationId, offboardingId, "TaskRejected", $"Task rejected: {task.Title}",
            JsonSerializer.Serialize(new { taskId, reason = request.Reason.Trim() }), actor.UserId, actor.Name, cancellationToken);
        Notify(actor, offboardingId, OffboardingTriggers.TaskRejected, OffboardingNotificationKeys.ForEvent(OffboardingTriggers.TaskRejected, taskId), taskId);

        await ReturnToProgressIfNoLongerClearedAsync(actor, header, cancellationToken);
        return await _repo.GetTaskAsync(actor.OrganizationId, offboardingId, taskId, cancellationToken);
    }

    public async Task<OffboardingTaskDto?> WaiveTaskAsync(HrActor actor, Guid offboardingId, Guid taskId, WaiveOffboardingTaskRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.ApproveClearance);
        var header = await RequireHeaderAsync(actor.OrganizationId, offboardingId, cancellationToken);
        RequireActive(header);

        if (string.IsNullOrWhiteSpace(request.Reason))
            throw EmployeeServiceException.Validation("A reason is required to waive a task.", "reason_required");
        if (request.Reason.Length > 1000)
            throw EmployeeServiceException.Validation("The reason must be 1000 characters or fewer.");

        var task = await _repo.GetTaskAsync(actor.OrganizationId, offboardingId, taskId, cancellationToken);
        if (task == null) return null;
        if (task.Status == OffboardingTaskStatuses.Cancelled)
            throw EmployeeServiceException.Conflict("Cancelled tasks cannot be waived.", "task_cancelled");
        if (task.Status == OffboardingTaskStatuses.Completed)
            throw EmployeeServiceException.Conflict("Completed tasks cannot be waived. Reopen the task first.", "task_completed");
        if (task.IsWaived)
            throw EmployeeServiceException.Conflict("The task is already waived.", "task_waived");

        if (!await _repo.WaiveTaskAsync(actor.OrganizationId, offboardingId, taskId, request.Reason.Trim(), cancellationToken))
            throw EmployeeServiceException.Conflict("The task changed in the meantime. Refresh and try again.", "task_conflict");

        await _repo.AddActivityAsync(actor.OrganizationId, offboardingId, "TaskWaived", $"Task waived: {task.Title}",
            JsonSerializer.Serialize(new { taskId, reason = request.Reason.Trim() }), actor.UserId, actor.Name, cancellationToken);

        return await _repo.GetTaskAsync(actor.OrganizationId, offboardingId, taskId, cancellationToken);
    }

    private static void RequirePendingApproval(OffboardingTaskDto task)
    {
        if (!task.RequiresApproval)
            throw EmployeeServiceException.Conflict("This task does not require approval.", "approval_not_required");
        if (task.Status != OffboardingTaskStatuses.Completed
            || !string.Equals(task.ApprovalStatus, OffboardingApprovalStatuses.Pending, StringComparison.OrdinalIgnoreCase))
            throw EmployeeServiceException.Conflict("Only completed tasks awaiting approval can be approved or rejected.", "approval_not_pending");
    }

    /// <summary>
    /// An offboarding pending clearance must keep satisfying the clearance gate. If a task change breaks it
    /// (reopened, rejected, newly added required task) the offboarding goes back to In Progress.
    /// </summary>
    private async Task ReturnToProgressIfNoLongerClearedAsync(HrActor actor, OffboardingHeader header, CancellationToken cancellationToken)
    {
        if (!header.Status.Equals(OffboardingStatuses.PendingClearance, StringComparison.OrdinalIgnoreCase)) return;

        var tasks = await _repo.ListTasksAsync(actor.OrganizationId, header.Id, cancellationToken);
        if (OffboardingValidation.CanSubmitClearance(tasks)) return;

        if (await _repo.TransitionStatusAsync(actor.OrganizationId, header.Id, OffboardingStatuses.PendingClearance,
                OffboardingStatuses.InProgress, null, null, actor.UserId, cancellationToken))
        {
            await _repo.AddActivityAsync(actor.OrganizationId, header.Id, "StatusChanged",
                "Offboarding returned to In Progress because a required task is no longer complete",
                JsonSerializer.Serialize(new { from = OffboardingStatuses.PendingClearance, to = OffboardingStatuses.InProgress }),
                actor.UserId, actor.Name, cancellationToken);
        }
    }

    // -------------------------------------------------------------------------
    // Assets
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<OffboardingAssetDto>> ListAssetsAsync(HrActor actor, Guid offboardingId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.View);
        await RequireHeaderAsync(actor.OrganizationId, offboardingId, cancellationToken);
        return await _repo.ListAssetsAsync(actor.OrganizationId, offboardingId, cancellationToken);
    }

    public async Task<OffboardingAssetDto> AddAssetAsync(HrActor actor, Guid offboardingId, CreateOffboardingAssetRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.ManageAssets);
        var header = await RequireHeaderAsync(actor.OrganizationId, offboardingId, cancellationToken);
        RequireActive(header);

        var errors = OffboardingValidation.ValidateAssetFields(request.AssetName, request.AssetType, request.SerialNumber, request.Notes);
        if (errors.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", errors));

        var id = await _repo.AddAssetAsync(actor.OrganizationId, offboardingId, new OffboardingAssetSpec
        {
            AssetName = request.AssetName.Trim(),
            AssetType = NullIfEmpty(request.AssetType),
            SerialNumber = NullIfEmpty(request.SerialNumber),
            ReturnRequired = request.ReturnRequired,
            ExpectedReturnDate = request.ExpectedReturnDate?.Date,
            Notes = NullIfEmpty(request.Notes)
        }, cancellationToken);

        await _repo.AddActivityAsync(actor.OrganizationId, offboardingId, "AssetAdded", $"Asset added: {request.AssetName.Trim()}",
            JsonSerializer.Serialize(new { assetId = id }), actor.UserId, actor.Name, cancellationToken);

        return await _repo.GetAssetAsync(actor.OrganizationId, offboardingId, id, cancellationToken)
               ?? throw EmployeeServiceException.NotFound("Asset was created but could not be loaded.");
    }

    public async Task<OffboardingAssetDto?> UpdateAssetAsync(HrActor actor, Guid offboardingId, Guid assetId, UpdateOffboardingAssetRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.ManageAssets);
        var header = await RequireHeaderAsync(actor.OrganizationId, offboardingId, cancellationToken);
        RequireActive(header);

        var errors = OffboardingValidation.ValidateAssetFields(request.AssetName, request.AssetType, request.SerialNumber, request.Notes);
        if (errors.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", errors));

        var existing = await _repo.GetAssetAsync(actor.OrganizationId, offboardingId, assetId, cancellationToken);
        if (existing == null) return null;

        if (!await _repo.UpdateAssetAsync(actor.OrganizationId, offboardingId, assetId, new OffboardingAssetSpec
        {
            AssetName = request.AssetName.Trim(),
            AssetType = NullIfEmpty(request.AssetType),
            SerialNumber = NullIfEmpty(request.SerialNumber),
            ReturnRequired = request.ReturnRequired,
            ExpectedReturnDate = request.ExpectedReturnDate?.Date,
            Notes = NullIfEmpty(request.Notes)
        }, cancellationToken))
            return null;

        await _repo.AddActivityAsync(actor.OrganizationId, offboardingId, "AssetUpdated", $"Asset updated: {request.AssetName.Trim()}",
            JsonSerializer.Serialize(new { assetId }), actor.UserId, actor.Name, cancellationToken);
        return await _repo.GetAssetAsync(actor.OrganizationId, offboardingId, assetId, cancellationToken);
    }

    public async Task<bool> DeleteAssetAsync(HrActor actor, Guid offboardingId, Guid assetId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.ManageAssets);
        var header = await RequireHeaderAsync(actor.OrganizationId, offboardingId, cancellationToken);
        RequireActive(header);

        var existing = await _repo.GetAssetAsync(actor.OrganizationId, offboardingId, assetId, cancellationToken);
        if (existing == null) return false;

        var deleted = await _repo.DeleteAssetAsync(actor.OrganizationId, offboardingId, assetId, cancellationToken);
        if (deleted)
        {
            await _repo.AddActivityAsync(actor.OrganizationId, offboardingId, "AssetRemoved", $"Asset removed: {existing.AssetName}",
                JsonSerializer.Serialize(new { assetId }), actor.UserId, actor.Name, cancellationToken);
        }
        return deleted;
    }

    public async Task<OffboardingAssetDto?> UpdateAssetStatusAsync(HrActor actor, Guid offboardingId, Guid assetId, UpdateOffboardingAssetStatusRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.ManageAssets);
        var header = await RequireHeaderAsync(actor.OrganizationId, offboardingId, cancellationToken);
        RequireActive(header);

        var errors = OffboardingValidation.ValidateAssetStatusChange(request);
        if (errors.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", errors));
        var status = OffboardingValidation.NormalizeAssetStatus(request.Status)!;

        var existing = await _repo.GetAssetAsync(actor.OrganizationId, offboardingId, assetId, cancellationToken);
        if (existing == null) return null;

        if (!await _repo.UpdateAssetStatusAsync(actor.OrganizationId, offboardingId, assetId, status, request, actor.UserId, cancellationToken))
            return null;

        await _repo.AddActivityAsync(actor.OrganizationId, offboardingId, "AssetStatusChanged", $"Asset {existing.AssetName}: {status}",
            JsonSerializer.Serialize(new { assetId, from = existing.Status, to = status }), actor.UserId, actor.Name, cancellationToken);
        return await _repo.GetAssetAsync(actor.OrganizationId, offboardingId, assetId, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Access
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<OffboardingAccessItemDto>> ListAccessItemsAsync(HrActor actor, Guid offboardingId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.View);
        await RequireHeaderAsync(actor.OrganizationId, offboardingId, cancellationToken);
        return await _repo.ListAccessItemsAsync(actor.OrganizationId, offboardingId, cancellationToken);
    }

    public async Task<OffboardingAccessItemDto> AddAccessItemAsync(HrActor actor, Guid offboardingId, CreateOffboardingAccessItemRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.ManageAccess);
        var header = await RequireHeaderAsync(actor.OrganizationId, offboardingId, cancellationToken);
        RequireActive(header);

        var errors = OffboardingValidation.ValidateAccessFields(request.SystemName, request.Notes);
        if (errors.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", errors));
        await EnsureUserInOrgAsync(actor.OrganizationId, request.ResponsibleUserId, "Responsible", cancellationToken);

        var id = await _repo.AddAccessItemAsync(actor.OrganizationId, offboardingId, new OffboardingAccessSpec
        {
            SystemName = request.SystemName.Trim(),
            ResponsibleUserId = request.ResponsibleUserId,
            ScheduledAt = request.ScheduledAt,
            Notes = NullIfEmpty(request.Notes)
        }, cancellationToken);

        await _repo.AddActivityAsync(actor.OrganizationId, offboardingId, "AccessItemAdded", $"Access item added: {request.SystemName.Trim()}",
            JsonSerializer.Serialize(new { accessItemId = id }), actor.UserId, actor.Name, cancellationToken);

        return await _repo.GetAccessItemAsync(actor.OrganizationId, offboardingId, id, cancellationToken)
               ?? throw EmployeeServiceException.NotFound("Access item was created but could not be loaded.");
    }

    public async Task<OffboardingAccessItemDto?> UpdateAccessItemAsync(HrActor actor, Guid offboardingId, Guid accessItemId, UpdateOffboardingAccessItemRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.ManageAccess);
        var header = await RequireHeaderAsync(actor.OrganizationId, offboardingId, cancellationToken);
        RequireActive(header);

        var errors = OffboardingValidation.ValidateAccessFields(request.SystemName, request.Notes);
        if (errors.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", errors));
        await EnsureUserInOrgAsync(actor.OrganizationId, request.ResponsibleUserId, "Responsible", cancellationToken);

        var existing = await _repo.GetAccessItemAsync(actor.OrganizationId, offboardingId, accessItemId, cancellationToken);
        if (existing == null) return null;

        if (!await _repo.UpdateAccessItemAsync(actor.OrganizationId, offboardingId, accessItemId, new OffboardingAccessSpec
        {
            SystemName = request.SystemName.Trim(),
            ResponsibleUserId = request.ResponsibleUserId,
            ScheduledAt = request.ScheduledAt,
            Notes = NullIfEmpty(request.Notes)
        }, cancellationToken))
            return null;

        await _repo.AddActivityAsync(actor.OrganizationId, offboardingId, "AccessItemUpdated", $"Access item updated: {request.SystemName.Trim()}",
            JsonSerializer.Serialize(new { accessItemId }), actor.UserId, actor.Name, cancellationToken);
        return await _repo.GetAccessItemAsync(actor.OrganizationId, offboardingId, accessItemId, cancellationToken);
    }

    public async Task<bool> DeleteAccessItemAsync(HrActor actor, Guid offboardingId, Guid accessItemId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.ManageAccess);
        var header = await RequireHeaderAsync(actor.OrganizationId, offboardingId, cancellationToken);
        RequireActive(header);

        var existing = await _repo.GetAccessItemAsync(actor.OrganizationId, offboardingId, accessItemId, cancellationToken);
        if (existing == null) return false;

        var deleted = await _repo.DeleteAccessItemAsync(actor.OrganizationId, offboardingId, accessItemId, cancellationToken);
        if (deleted)
        {
            await _repo.AddActivityAsync(actor.OrganizationId, offboardingId, "AccessItemRemoved", $"Access item removed: {existing.SystemName}",
                JsonSerializer.Serialize(new { accessItemId }), actor.UserId, actor.Name, cancellationToken);
        }
        return deleted;
    }

    public async Task<OffboardingAccessItemDto?> UpdateAccessItemStatusAsync(HrActor actor, Guid offboardingId, Guid accessItemId, UpdateOffboardingAccessStatusRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.ManageAccess);
        var header = await RequireHeaderAsync(actor.OrganizationId, offboardingId, cancellationToken);
        RequireActive(header);

        var errors = OffboardingValidation.ValidateAccessStatusRequest(request);
        if (errors.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", errors));
        var status = OffboardingValidation.NormalizeAccessStatus(request.Status)!;

        var existing = await _repo.GetAccessItemAsync(actor.OrganizationId, offboardingId, accessItemId, cancellationToken);
        if (existing == null) return null;

        var transitionError = OffboardingValidation.ValidateAccessStatusChange(existing.Status, status);
        if (transitionError != null) throw EmployeeServiceException.Conflict(transitionError, "invalid_access_transition");

        if (!await _repo.UpdateAccessItemStatusAsync(actor.OrganizationId, offboardingId, accessItemId, status, request, actor.UserId, cancellationToken))
            return null;

        await _repo.AddActivityAsync(actor.OrganizationId, offboardingId, "AccessStatusChanged", $"Access {existing.SystemName}: {status}",
            JsonSerializer.Serialize(new { accessItemId, from = existing.Status, to = status }), actor.UserId, actor.Name, cancellationToken);
        return await _repo.GetAccessItemAsync(actor.OrganizationId, offboardingId, accessItemId, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Settlement
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<OffboardingSettlementItemDto>> ListSettlementItemsAsync(HrActor actor, Guid offboardingId, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, OffboardingPermissions.View);
        await RequireHeaderAsync(actor.OrganizationId, offboardingId, cancellationToken);
        var items = await _repo.ListSettlementItemsAsync(actor.OrganizationId, offboardingId, cancellationToken);
        if (!CanSeeSettlementDetails(codes))
        {
            foreach (var item in items) RedactSettlementItem(item);
        }
        return items;
    }

    public async Task<OffboardingSettlementItemDto?> UpdateSettlementItemAsync(HrActor actor, Guid offboardingId, Guid itemId, UpdateOffboardingSettlementItemRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.ManageSettlement);
        var header = await RequireHeaderAsync(actor.OrganizationId, offboardingId, cancellationToken);
        RequireActive(header);

        var errors = OffboardingValidation.ValidateSettlementUpdate(request);
        if (errors.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", errors));
        var status = OffboardingValidation.NormalizeSettlementStatus(request.Status)!;

        var existing = await _repo.GetSettlementItemAsync(actor.OrganizationId, offboardingId, itemId, cancellationToken);
        if (existing == null) return null;

        if (!await _repo.UpdateSettlementItemAsync(actor.OrganizationId, offboardingId, itemId, status, request, actor.UserId, cancellationToken))
            return null;

        // Amounts and notes are financial / confidential: only the item and its status go to the activity log.
        await _repo.AddActivityAsync(actor.OrganizationId, offboardingId, "SettlementUpdated", $"Settlement item {existing.Title}: {status}",
            JsonSerializer.Serialize(new { itemId, from = existing.Status, to = status }), actor.UserId, actor.Name, cancellationToken);
        return await _repo.GetSettlementItemAsync(actor.OrganizationId, offboardingId, itemId, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Exit interview
    // -------------------------------------------------------------------------

    public async Task<OffboardingExitInterviewDto?> GetExitInterviewAsync(HrActor actor, Guid offboardingId, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, OffboardingPermissions.View);
        await RequireHeaderAsync(actor.OrganizationId, offboardingId, cancellationToken);
        var interview = await _repo.GetExitInterviewAsync(actor.OrganizationId, offboardingId, cancellationToken);
        if (interview != null && !CanViewConfidential(codes)) RedactExitInterview(interview);
        return interview;
    }

    public async Task<OffboardingExitInterviewDto?> UpdateExitInterviewAsync(HrActor actor, Guid offboardingId, UpdateOffboardingExitInterviewRequest request, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, OffboardingPermissions.ManageExitInterviews);
        var header = await RequireHeaderAsync(actor.OrganizationId, offboardingId, cancellationToken);
        RequireActive(header);

        // Responses and HR notes are confidential: callers without the capability can schedule but never read or overwrite them.
        var canConfidential = CanViewConfidential(codes);
        if (!canConfidential)
        {
            request.ResponsesJson = null;
            request.HrNotes = null;
        }

        var errors = OffboardingValidation.ValidateExitInterview(request);
        if (request.HrNotes is { Length: > 20000 }) errors.Add("HR notes must be 20000 characters or fewer.");
        if (errors.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", errors));
        var status = OffboardingValidation.NormalizeExitInterviewStatus(request.Status)!;
        await EnsureUserInOrgAsync(actor.OrganizationId, request.InterviewerUserId, "Interviewer", cancellationToken);

        if (!await _repo.UpdateExitInterviewAsync(actor.OrganizationId, offboardingId, status, request, canConfidential, cancellationToken))
            return null;

        // Status only: responses and notes never reach the activity log.
        await _repo.AddActivityAsync(actor.OrganizationId, offboardingId, "ExitInterviewUpdated", $"Exit interview: {status}",
            JsonSerializer.Serialize(new { status }), actor.UserId, actor.Name, cancellationToken);

        var interview = await _repo.GetExitInterviewAsync(actor.OrganizationId, offboardingId, cancellationToken);
        if (interview != null && !canConfidential) RedactExitInterview(interview);
        return interview;
    }

    // -------------------------------------------------------------------------
    // Documents
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<OffboardingDocumentDto>> ListDocumentsAsync(HrActor actor, Guid offboardingId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.View);
        await RequireHeaderAsync(actor.OrganizationId, offboardingId, cancellationToken);
        return await _repo.ListDocumentsAsync(actor.OrganizationId, offboardingId, cancellationToken);
    }

    public async Task<OffboardingDocumentDto?> UpdateDocumentAsync(HrActor actor, Guid offboardingId, Guid documentId, UpdateOffboardingDocumentRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.ManageDocuments);
        var header = await RequireHeaderAsync(actor.OrganizationId, offboardingId, cancellationToken);
        RequireActive(header);

        var status = OffboardingValidation.NormalizeDocumentStatus(request.Status)
                     ?? throw EmployeeServiceException.Validation(
                         $"Document status must be one of: {string.Join(", ", OffboardingDocumentStatuses.All.OrderBy(s => s))}.");

        var doc = await _repo.GetDocumentAsync(actor.OrganizationId, offboardingId, documentId, cancellationToken);
        if (doc == null) return null;

        if (!await _repo.UpdateDocumentAsync(actor.OrganizationId, offboardingId, documentId, status, request.EmployeeDocumentId, cancellationToken))
            return null;

        await _repo.AddActivityAsync(actor.OrganizationId, offboardingId, "DocumentUpdated", $"Document {doc.Name}: {status}",
            JsonSerializer.Serialize(new { documentId, from = doc.Status, to = status }), actor.UserId, actor.Name, cancellationToken);
        return await _repo.GetDocumentAsync(actor.OrganizationId, offboardingId, documentId, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Templates
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<OffboardingTemplateListItemDto>> ListTemplatesAsync(HrActor actor, OffboardingTemplateListQuery query, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.View);
        await EnsureOrgReadyAsync(actor.OrganizationId, actor.UserId, cancellationToken);
        query.SeparationType = NormalizeSeparationFilter(query.SeparationType);
        return await _repo.ListTemplatesAsync(actor.OrganizationId, query, cancellationToken);
    }

    public async Task<OffboardingTemplateDetailDto?> GetTemplateAsync(HrActor actor, Guid templateId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.View);
        return await _repo.GetTemplateAsync(actor.OrganizationId, templateId, cancellationToken);
    }

    public async Task<OffboardingTemplateDetailDto> CreateTemplateAsync(HrActor actor, UpsertOffboardingTemplateRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.ManageTemplates);
        await EnsureOrgReadyAsync(actor.OrganizationId, actor.UserId, cancellationToken);
        await ValidateTemplateAsync(actor, request, null, cancellationToken);

        var id = await _repo.CreateTemplateAsync(actor.OrganizationId, request, actor.UserId, cancellationToken);
        return await _repo.GetTemplateAsync(actor.OrganizationId, id, cancellationToken)
               ?? throw EmployeeServiceException.NotFound("Template was created but could not be loaded.");
    }

    public async Task<OffboardingTemplateDetailDto?> UpdateTemplateAsync(HrActor actor, Guid templateId, UpsertOffboardingTemplateRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.ManageTemplates);

        var existing = await _repo.GetTemplateAsync(actor.OrganizationId, templateId, cancellationToken);
        if (existing == null) return null;
        if (existing.IsArchived)
            throw EmployeeServiceException.Conflict("Archived templates cannot be edited.", "template_archived");

        await ValidateTemplateAsync(actor, request, templateId, cancellationToken);

        // Existing offboardings keep their snapshot; only future offboardings use the new version.
        if (!await _repo.UpdateTemplateAsync(actor.OrganizationId, templateId, request, actor.UserId, cancellationToken)) return null;
        return await _repo.GetTemplateAsync(actor.OrganizationId, templateId, cancellationToken);
    }

    public async Task<OffboardingTemplateDetailDto?> DuplicateTemplateAsync(HrActor actor, Guid templateId, DuplicateOffboardingTemplateRequest? request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.ManageTemplates);

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
        await RequireAsync(actor, OffboardingPermissions.ManageTemplates);
        return await _repo.ArchiveTemplateAsync(actor.OrganizationId, templateId, actor.UserId, cancellationToken);
    }

    public async Task<OffboardingTemplateDetailDto?> SetDefaultTemplateAsync(HrActor actor, Guid templateId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.ManageTemplates);

        var existing = await _repo.GetTemplateAsync(actor.OrganizationId, templateId, cancellationToken);
        if (existing == null) return null;
        if (existing.IsArchived || !existing.IsActive)
            throw EmployeeServiceException.Conflict("Only an active, non-archived template can be the default.", "template_inactive");

        if (!await _repo.SetDefaultTemplateAsync(actor.OrganizationId, templateId, actor.UserId, cancellationToken)) return null;
        return await _repo.GetTemplateAsync(actor.OrganizationId, templateId, cancellationToken);
    }

    private async Task ValidateTemplateAsync(HrActor actor, UpsertOffboardingTemplateRequest request, Guid? excludeId, CancellationToken cancellationToken)
    {
        request.Tasks ??= new List<UpsertOffboardingTemplateTaskRequest>();
        var errors = OffboardingValidation.ValidateTemplate(request);
        if (errors.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", errors));

        request.Name = request.Name.Trim();
        request.SeparationType = OffboardingValidation.NormalizeSeparationType(request.SeparationType);
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

    public async Task<IReadOnlyList<OffboardingNotificationSettingDto>> GetNotificationSettingsAsync(HrActor actor, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.View);
        await EnsureOrgReadyAsync(actor.OrganizationId, actor.UserId, cancellationToken);
        return await _repo.GetNotificationSettingsAsync(actor.OrganizationId, cancellationToken);
    }

    public async Task<IReadOnlyList<OffboardingNotificationSettingDto>> UpdateNotificationSettingsAsync(
        HrActor actor, UpdateOffboardingNotificationSettingsRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.ManageAutomations);
        await EnsureOrgReadyAsync(actor.OrganizationId, actor.UserId, cancellationToken);

        var errors = new List<string>();
        foreach (var setting in request.Settings ?? [])
        {
            var trigger = OffboardingTriggers.All.FirstOrDefault(t => t.Equals(setting.TriggerCode?.Trim(), StringComparison.OrdinalIgnoreCase));
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
                    var role = OffboardingValidation.NormalizeFromSet(OffboardingRecipientRoles.All, r);
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

    public async Task<IReadOnlyList<OffboardingNotificationDto>> ListNotificationsAsync(HrActor actor, Guid? offboardingId, int take, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OffboardingPermissions.ManageAutomations);
        return await _repo.ListNotificationsAsync(actor.OrganizationId, offboardingId, take <= 0 ? 50 : Math.Min(take, 500), cancellationToken);
    }

    /// <summary>Fire-and-forget (deduplicated downstream); a notification problem must never fail the business operation.</summary>
    private void Notify(HrActor actor, Guid offboardingId, string trigger, string? dedupeKey = null, Guid? taskId = null)
    {
        try
        {
            _notifications.Enqueue(new OffboardingNotificationEvent(
                actor.OrganizationId, offboardingId, trigger,
                dedupeKey ?? OffboardingNotificationKeys.ForEvent(trigger, offboardingId), taskId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enqueue offboarding notification {Trigger}.", trigger);
        }
    }

    private async Task NotifyTaskAssignmentsAsync(HrActor actor, Guid offboardingId, CancellationToken cancellationToken)
    {
        try
        {
            var tasks = await _repo.ListTasksAsync(actor.OrganizationId, offboardingId, cancellationToken);
            foreach (var task in tasks.Where(t => t.AssignedUserId.HasValue
                                                  && t.Status != OffboardingTaskStatuses.Completed
                                                  && t.Status != OffboardingTaskStatuses.Cancelled
                                                  && !t.IsWaived))
            {
                Notify(actor, offboardingId, OffboardingTriggers.TaskAssigned,
                    OffboardingNotificationKeys.ForTask(OffboardingTriggers.TaskAssigned, task.Id, task.AssignedUserId), task.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not queue task assignment notifications for offboarding {OffboardingId}.", offboardingId);
        }
    }

    // -------------------------------------------------------------------------
    // Confidentiality
    // -------------------------------------------------------------------------

    private static bool CanViewConfidential(IReadOnlyList<string> codes) =>
        OffboardingPermissions.Has(codes, OffboardingPermissions.ViewConfidentialInformation);

    private static bool CanSeeSettlementDetails(IReadOnlyList<string> codes) =>
        OffboardingPermissions.Has(codes, OffboardingPermissions.ManageSettlement) || CanViewConfidential(codes);

    private static void RedactExitInterview(OffboardingExitInterviewDto interview)
    {
        interview.ResponsesJson = null;
        interview.HrNotes = null;
        interview.ConfidentialRedacted = true;
    }

    private static void RedactSettlementItem(OffboardingSettlementItemDto item)
    {
        item.Amount = null;
        item.Currency = null;
        item.Notes = null;
        item.EvidenceUrl = null;
    }

    /// <summary>Strips confidential fields the caller is not entitled to see (the repository always returns everything).</summary>
    private static OffboardingDetailDto Redact(OffboardingDetailDto detail, IReadOnlyList<string> codes)
    {
        if (!CanViewConfidential(codes))
        {
            detail.SeparationReason = null;
            detail.ConfidentialNotes = null;
            detail.ConfidentialRedacted = true;
            if (detail.ExitInterview != null) RedactExitInterview(detail.ExitInterview);
        }
        if (!CanSeeSettlementDetails(codes))
        {
            foreach (var item in detail.SettlementItems) RedactSettlementItem(item);
        }
        return detail;
    }

    private async Task<OffboardingDetailDto?> LoadDetailAsync(HrActor actor, IReadOnlyList<string> codes, Guid offboardingId, CancellationToken cancellationToken)
    {
        var detail = await _repo.GetAsync(actor.OrganizationId, offboardingId, cancellationToken);
        return detail == null ? null : Redact(detail, codes);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task<OffboardingHeader> RequireHeaderAsync(Guid organizationId, Guid offboardingId, CancellationToken cancellationToken) =>
        await _repo.GetHeaderAsync(organizationId, offboardingId, cancellationToken)
        ?? throw EmployeeServiceException.NotFound("Offboarding not found.");

    private static void RequireActive(OffboardingHeader header)
    {
        if (OffboardingValidation.IsTerminalStatus(header.Status))
            throw EmployeeServiceException.Conflict($"A {header.Status} offboarding can no longer be modified.", "offboarding_closed");
    }

    private async Task EnsureUserInOrgAsync(Guid organizationId, Guid? userId, string label, CancellationToken cancellationToken)
    {
        if (userId.HasValue && !await _repo.OrgUserExistsAsync(organizationId, userId.Value, cancellationToken))
            throw EmployeeServiceException.Validation($"{label} user not found in this organization.");
    }

    private static string? NormalizeSeparationFilter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return OffboardingValidation.NormalizeSeparationType(value)
               ?? throw EmployeeServiceException.Validation(
                   $"Separation type filter must be one of: {string.Join(", ", SeparationTypes.All.OrderBy(s => s))}.");
    }

    private static bool IsUniqueViolation(SqlException ex) => ex.Number is 2601 or 2627;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static OffboardingTaskSpec ToSpec(CreateOffboardingTaskRequest r) => new()
    {
        Title = r.Title.Trim(),
        Description = r.Description,
        Category = OffboardingValidation.NormalizeCategory(r.Category) ?? OffboardingTaskCategories.Other,
        AssignmentType = OffboardingValidation.NormalizeAssignmentType(r.AssignmentType) ?? OffboardingAssignmentTypes.SpecificUser,
        AssignedUserId = r.AssignedUserId,
        DueDate = r.DueDate?.Date,
        IsRequired = r.IsRequired,
        RequiresDocument = r.RequiresDocument,
        DocumentCategory = r.DocumentCategory,
        RequiresApproval = r.RequiresApproval,
        DependsOnTaskId = r.DependsOnTaskId
    };

    private static OffboardingTaskSpec ToSpec(UpdateOffboardingTaskRequest r) => new()
    {
        Title = r.Title.Trim(),
        Description = r.Description,
        Category = OffboardingValidation.NormalizeCategory(r.Category) ?? OffboardingTaskCategories.Other,
        AssignmentType = OffboardingValidation.NormalizeAssignmentType(r.AssignmentType) ?? OffboardingAssignmentTypes.SpecificUser,
        AssignedUserId = r.AssignedUserId,
        DueDate = r.DueDate?.Date,
        IsRequired = r.IsRequired,
        RequiresDocument = r.RequiresDocument,
        DocumentCategory = r.DocumentCategory,
        RequiresApproval = r.RequiresApproval ?? false
    };
}
