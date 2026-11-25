using System.Collections.Concurrent;
using System.Text.Json;
using nexthire_api.DTOs;
using nexthire_api.DTOs.CoreHr;
using nexthire_api.Exceptions;
using nexthire_api.Repositories;
using nexthire_api.Security;

namespace nexthire_api.Services;

public class OrganizationService : IOrganizationService
{
    private const string DefaultOrganizationName = "Organization";

    // Orgs whose development defaults / demo data were already ensured by this process.
    private static readonly ConcurrentDictionary<Guid, bool> OrgInitialized = new();

    private readonly IOrganizationRepository _repo;
    private readonly IEmployeeRepository _employees;
    private readonly IRoleRepository _roles;
    private readonly IHostEnvironment _env;
    private readonly ILogger<OrganizationService> _logger;

    public OrganizationService(
        IOrganizationRepository repo,
        IEmployeeRepository employees,
        IRoleRepository roles,
        IHostEnvironment env,
        ILogger<OrganizationService> logger)
    {
        _repo = repo;
        _employees = employees;
        _roles = roles;
        _env = env;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // Permissions / setup
    // -------------------------------------------------------------------------

    private async Task RequireAsync(HrActor actor, string capability)
    {
        var codes = await _roles.ListRoleCodesByNexaUserAsync(actor.OrganizationId, actor.UserId);
        if (!OrganizationPermissions.Has(codes, capability))
            throw new HrAccessDeniedException(capability);
    }

    public async Task<IReadOnlyList<string>> GetCapabilitiesAsync(HrActor actor, CancellationToken cancellationToken = default)
    {
        var codes = await _roles.ListRoleCodesByNexaUserAsync(actor.OrganizationId, actor.UserId);
        return OrganizationPermissions.ListFor(codes);
    }

    /// <summary>Always ensures default levels; in development also seeds demo employees and a demo org structure.</summary>
    private async Task EnsureOrgReadyAsync(HrActor actor, CancellationToken cancellationToken)
    {
        if (OrgInitialized.ContainsKey(actor.OrganizationId)) return;

        if (_env.IsDevelopment())
        {
            await _employees.EnsureOrgDefaultsAsync(actor.OrganizationId, cancellationToken);
            await _employees.SeedDemoEmployeesIfEmptyAsync(actor.OrganizationId, actor.UserId, true, cancellationToken);
        }

        await _repo.EnsureOrgLevelsAsync(actor.OrganizationId, actor.UserId, cancellationToken);

        if (_env.IsDevelopment())
            await _repo.SeedDemoOrganizationIfEmptyAsync(actor.OrganizationId, actor.UserId, true, cancellationToken);

        OrgInitialized[actor.OrganizationId] = true;
    }

    private static DateTime Today() => DateTime.UtcNow.Date;

    private async Task LogAsync(
        HrActor actor, string changeType, string entityType, Guid? entityId, string summary,
        Guid? departmentId = null, Guid? employeeId = null, Guid? positionId = null,
        object? previous = null, object? current = null, DateTime? effectiveDate = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _repo.InsertActivityAsync(
                actor.OrganizationId, changeType, entityType, entityId, departmentId, employeeId, positionId, summary,
                previous == null ? null : JsonSerializer.Serialize(previous),
                current == null ? null : JsonSerializer.Serialize(current),
                effectiveDate, actor.UserId, actor.Name, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // History must never fail the operation that was already committed.
            _logger.LogWarning(ex, "Failed to record organization activity {ChangeType} for organization {OrganizationId}.", changeType, actor.OrganizationId);
        }
    }

    // -------------------------------------------------------------------------
    // Summary / charts
    // -------------------------------------------------------------------------

    public async Task<OrganizationSummaryDto> GetSummaryAsync(HrActor actor, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OrganizationPermissions.View);
        await EnsureOrgReadyAsync(actor, cancellationToken);
        return await _repo.GetSummaryAsync(actor.OrganizationId, Today(), cancellationToken);
    }

    public async Task<OrgDepartmentChartDto> GetDepartmentChartAsync(HrActor actor, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OrganizationPermissions.View);
        await EnsureOrgReadyAsync(actor, cancellationToken);
        return await _repo.GetDepartmentChartAsync(actor.OrganizationId, DefaultOrganizationName, cancellationToken);
    }

    public async Task<OrgPeopleChartDto> GetPeopleChartAsync(HrActor actor, Guid? rootEmployeeId, int? maxDepth, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OrganizationPermissions.ViewPeople);
        await EnsureOrgReadyAsync(actor, cancellationToken);
        return await _repo.GetPeopleChartAsync(actor.OrganizationId, DefaultOrganizationName, rootEmployeeId, maxDepth, cancellationToken);
    }

    public async Task<OrgPositionChartDto> GetPositionChartAsync(HrActor actor, Guid? departmentId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OrganizationPermissions.View);
        await EnsureOrgReadyAsync(actor, cancellationToken);
        return await _repo.GetPositionChartAsync(actor.OrganizationId, DefaultOrganizationName, departmentId, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Departments
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<OrgDepartmentListItemDto>> ListDepartmentsAsync(HrActor actor, bool includeArchived, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OrganizationPermissions.View);
        await EnsureOrgReadyAsync(actor, cancellationToken);
        return await _repo.ListDepartmentsAsync(actor.OrganizationId, includeArchived, cancellationToken);
    }

    public async Task<OrgDepartmentDetailDto?> GetDepartmentAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OrganizationPermissions.View);
        return await _repo.GetDepartmentAsync(actor.OrganizationId, id, cancellationToken);
    }

    public async Task<OrgDepartmentSummaryDto?> GetDepartmentSummaryAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OrganizationPermissions.View);
        return await _repo.GetDepartmentSummaryAsync(actor.OrganizationId, id, Today(), cancellationToken);
    }

    public async Task<OrgDepartmentDetailDto> CreateDepartmentAsync(HrActor actor, CreateOrgDepartmentRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OrganizationPermissions.ManageDepartments);

        var row = await BuildDepartmentRowAsync(actor.OrganizationId, request, null, cancellationToken);
        var id = await _repo.InsertDepartmentAsync(actor.OrganizationId, row, actor.UserId, cancellationToken);

        await LogAsync(actor, "DepartmentCreated", "Department", id, $"Department created: {row.Name}",
            departmentId: id, current: new { row.Name, row.ParentDepartmentId, row.HeadEmployeeId }, cancellationToken: cancellationToken);

        return await _repo.GetDepartmentAsync(actor.OrganizationId, id, cancellationToken)
               ?? throw EmployeeServiceException.NotFound("Department was created but could not be loaded.");
    }

    public async Task<OrgDepartmentDetailDto?> UpdateDepartmentAsync(HrActor actor, Guid id, UpdateOrgDepartmentRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OrganizationPermissions.ManageDepartments);

        var existing = await _repo.GetDepartmentAsync(actor.OrganizationId, id, cancellationToken);
        if (existing == null) return null;

        var row = await BuildDepartmentRowAsync(actor.OrganizationId, request, existing, cancellationToken);

        if (row.ParentDepartmentId != existing.ParentDepartmentId
            && await _repo.WouldCreateDepartmentCycleAsync(actor.OrganizationId, id, row.ParentDepartmentId, cancellationToken))
            throw EmployeeServiceException.Validation("A department cannot be moved under itself or one of its sub-departments.");

        if (request.IsActive == false && existing.IsActive)
            await EnsureCanArchiveDepartmentAsync(actor.OrganizationId, existing, cancellationToken);

        var updated = await _repo.UpdateDepartmentAsync(actor.OrganizationId, id, row, request.IsActive, actor.UserId, cancellationToken);
        if (!updated) return null;

        await LogAsync(actor, "DepartmentUpdated", "Department", id, $"Department updated: {row.Name}",
            departmentId: id,
            previous: new { existing.Name, existing.ParentDepartmentId, existing.HeadEmployeeId, existing.Color, existing.Icon },
            current: new { row.Name, row.ParentDepartmentId, row.HeadEmployeeId, row.Color, row.Icon },
            cancellationToken: cancellationToken);

        if (row.HeadEmployeeId != existing.HeadEmployeeId)
        {
            await LogAsync(actor, "DepartmentHeadChanged", "Department", id, $"Department head changed: {row.Name}",
                departmentId: id, employeeId: row.HeadEmployeeId,
                previous: new { existing.HeadEmployeeId }, current: new { row.HeadEmployeeId },
                cancellationToken: cancellationToken);
        }

        return await _repo.GetDepartmentAsync(actor.OrganizationId, id, cancellationToken);
    }

    public async Task<OrgDepartmentDetailDto?> MoveDepartmentAsync(HrActor actor, Guid id, MoveOrgDepartmentRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OrganizationPermissions.ManageDepartments);

        var existing = await _repo.GetDepartmentAsync(actor.OrganizationId, id, cancellationToken);
        if (existing == null) return null;

        var newParent = request.NewParentDepartmentId;
        if (newParent.HasValue && !await _repo.DepartmentExistsAsync(actor.OrganizationId, newParent.Value, cancellationToken))
            throw EmployeeServiceException.NotFound("The new parent department was not found.");

        if (await _repo.WouldCreateDepartmentCycleAsync(actor.OrganizationId, id, newParent, cancellationToken))
            throw EmployeeServiceException.Validation("A department cannot be moved under itself or one of its sub-departments.");

        if (existing.ParentDepartmentId == newParent) return existing;

        if (!await _repo.MoveDepartmentAsync(actor.OrganizationId, id, newParent, actor.UserId, cancellationToken))
            return null;

        var moved = await _repo.GetDepartmentAsync(actor.OrganizationId, id, cancellationToken);
        await LogAsync(actor, "DepartmentMoved", "Department", id,
            $"Department moved: {existing.Name} → {moved?.ParentDepartmentName ?? "top level"}",
            departmentId: id,
            previous: new { ParentDepartmentId = existing.ParentDepartmentId, ParentDepartmentName = existing.ParentDepartmentName },
            current: new { ParentDepartmentId = newParent, ParentDepartmentName = moved?.ParentDepartmentName },
            effectiveDate: request.EffectiveDate, cancellationToken: cancellationToken);
        return moved;
    }

    public async Task<bool> ArchiveDepartmentAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OrganizationPermissions.ManageDepartments);

        var existing = await _repo.GetDepartmentAsync(actor.OrganizationId, id, cancellationToken);
        if (existing == null) return false;

        await EnsureCanArchiveDepartmentAsync(actor.OrganizationId, existing, cancellationToken);

        if (!await _repo.ArchiveDepartmentAsync(actor.OrganizationId, id, actor.UserId, cancellationToken))
            return false;

        await LogAsync(actor, "DepartmentArchived", "Department", id, $"Department archived: {existing.Name}",
            departmentId: id, previous: new { existing.Name }, cancellationToken: cancellationToken);
        return true;
    }

    private async Task EnsureCanArchiveDepartmentAsync(Guid organizationId, OrgDepartmentDetailDto department, CancellationToken cancellationToken)
    {
        var employees = await _repo.CountActiveEmployeesInDepartmentAsync(organizationId, department.Id, cancellationToken);
        if (employees > 0)
            throw EmployeeServiceException.Validation(
                $"{department.Name} still has {employees} active employee(s). Reassign them to another department before archiving.");

        var positions = await _repo.CountActivePositionsInDepartmentAsync(organizationId, department.Id, cancellationToken);
        if (positions > 0)
            throw EmployeeServiceException.Validation(
                $"{department.Name} still has {positions} active position(s). Move or archive them before archiving the department.");

        if (department.TeamCount > 0)
            throw EmployeeServiceException.Validation(
                $"{department.Name} still has {department.TeamCount} sub-department(s). Move or archive them first.");
    }

    private async Task<OrgDepartmentInsert> BuildDepartmentRowAsync(
        Guid organizationId, CreateOrgDepartmentRequest request, OrgDepartmentDetailDto? existing, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name)) throw EmployeeServiceException.Validation("Name is required.");
        if (name.Length > 200) throw EmployeeServiceException.Validation("Name must be 200 characters or fewer.");
        if (request.Code is { Length: > 50 }) throw EmployeeServiceException.Validation("Code must be 50 characters or fewer.");
        if (request.Description is { Length: > 1000 }) throw EmployeeServiceException.Validation("Description must be 1000 characters or fewer.");
        if (request.CostCenter is { Length: > 80 }) throw EmployeeServiceException.Validation("Cost center must be 80 characters or fewer.");

        string? color;
        if (string.IsNullOrWhiteSpace(request.Color))
            color = null;
        else
        {
            color = OrganizationValidation.NormalizeColor(request.Color)
                    ?? throw EmployeeServiceException.Validation("Color must be a hex value such as #6366F1.");
        }

        if (existing != null && request.ParentDepartmentId == existing.Id)
            throw EmployeeServiceException.Validation("A department cannot be its own parent.");

        if (request.ParentDepartmentId.HasValue
            && !await _repo.DepartmentExistsAsync(organizationId, request.ParentDepartmentId.Value, cancellationToken))
            throw EmployeeServiceException.NotFound("The parent department was not found.");

        if (request.HeadEmployeeId.HasValue
            && !await _repo.EmployeeExistsAsync(organizationId, request.HeadEmployeeId.Value, cancellationToken))
            throw EmployeeServiceException.NotFound("The department head (employee) was not found.");

        return new OrgDepartmentInsert(
            name,
            request.Code,
            request.Description,
            request.ParentDepartmentId,
            request.HeadEmployeeId,
            request.CostCenter,
            color,
            request.Icon == null && existing != null ? existing.Icon : OrganizationValidation.NormalizeIcon(request.Icon),
            request.DisplayOrder ?? existing?.DisplayOrder ?? 0);
    }

    // -------------------------------------------------------------------------
    // Positions
    // -------------------------------------------------------------------------

    public async Task<PagedResult<OrgPositionListItemDto>> ListPositionsAsync(HrActor actor, OrgPositionListQuery query, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OrganizationPermissions.View);

        if (!string.IsNullOrWhiteSpace(query.Status) && OrganizationValidation.NormalizePositionStatus(query.Status) == null)
            throw EmployeeServiceException.Validation($"Status must be one of: {string.Join(", ", OrganizationPositionStatuses.All)}.");

        await EnsureOrgReadyAsync(actor, cancellationToken);
        return await _repo.ListPositionsAsync(actor.OrganizationId, query, cancellationToken);
    }

    public async Task<OrgPositionDetailDto?> GetPositionAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OrganizationPermissions.View);
        return await _repo.GetPositionAsync(actor.OrganizationId, id, cancellationToken);
    }

    public async Task<OrgPositionDetailDto> CreatePositionAsync(HrActor actor, CreateOrgPositionRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OrganizationPermissions.ManagePositions);

        var row = await BuildPositionRowAsync(actor.OrganizationId, request, null, cancellationToken);
        var id = await _repo.InsertPositionAsync(actor.OrganizationId, row, actor.UserId, cancellationToken);

        await LogAsync(actor, "PositionCreated", "Position", id, $"Position created: {row.PositionName}",
            departmentId: row.DepartmentId, positionId: id,
            current: new { row.PositionName, row.DepartmentId, row.ApprovedHeadcount, row.Status },
            effectiveDate: row.EffectiveDate, cancellationToken: cancellationToken);

        return await _repo.GetPositionAsync(actor.OrganizationId, id, cancellationToken)
               ?? throw EmployeeServiceException.NotFound("Position was created but could not be loaded.");
    }

    public async Task<OrgPositionDetailDto?> UpdatePositionAsync(HrActor actor, Guid id, UpdateOrgPositionRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OrganizationPermissions.ManagePositions);

        var existing = await _repo.GetPositionAsync(actor.OrganizationId, id, cancellationToken);
        if (existing == null || !existing.IsActive) return null;

        var row = await BuildPositionRowAsync(actor.OrganizationId, request, existing, cancellationToken);

        if (row.ApprovedHeadcount < existing.FilledCount)
            throw EmployeeServiceException.Validation(
                $"Headcount cannot be lower than the {existing.FilledCount} seat(s) currently filled. Unassign employees first.");

        if (row.ReportsToPositionId != existing.ReportsToPositionId
            && await _repo.WouldCreatePositionCycleAsync(actor.OrganizationId, id, row.ReportsToPositionId, cancellationToken))
            throw EmployeeServiceException.Validation("A position cannot report to itself or to one of its own reports.");

        if (!await _repo.UpdatePositionAsync(actor.OrganizationId, id, row, actor.UserId, cancellationToken))
            return null;

        await LogAsync(actor, "PositionUpdated", "Position", id, $"Position updated: {row.PositionName}",
            departmentId: row.DepartmentId, positionId: id,
            previous: new { Title = existing.Title, existing.DepartmentId, Headcount = existing.Headcount, existing.Status },
            current: new { Title = row.PositionName, row.DepartmentId, Headcount = row.ApprovedHeadcount, row.Status },
            cancellationToken: cancellationToken);

        return await _repo.GetPositionAsync(actor.OrganizationId, id, cancellationToken);
    }

    public async Task<OrgPositionDetailDto?> AssignPositionAsync(HrActor actor, Guid id, AssignOrgPositionRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OrganizationPermissions.AssignEmployees);

        if (request.EmployeeId == Guid.Empty) throw EmployeeServiceException.Validation("employeeId is required.");
        if (request.AllocationPct is <= 0 or > 100) throw EmployeeServiceException.Validation("Allocation must be between 1 and 100.");

        var position = await _repo.GetPositionAsync(actor.OrganizationId, id, cancellationToken);
        if (position == null) return null;
        if (!await _repo.EmployeeExistsAsync(actor.OrganizationId, request.EmployeeId, cancellationToken))
            throw EmployeeServiceException.NotFound("Employee not found.");

        var effective = (request.EffectiveDate ?? Today()).Date;
        var assignmentId = await _repo.AssignPositionAsync(
            actor.OrganizationId, id, request.EmployeeId, request.AllocationPct, request.IsPrimary, effective, actor.UserId, cancellationToken);

        var employeeName = await GetEmployeeNameAsync(actor.OrganizationId, request.EmployeeId, cancellationToken);
        await LogAsync(actor, "PositionAssigned", "Position", id, $"{employeeName} assigned to {position.Title}",
            departmentId: position.DepartmentId, employeeId: request.EmployeeId, positionId: id,
            current: new { AssignmentId = assignmentId, request.EmployeeId, request.AllocationPct, request.IsPrimary },
            effectiveDate: effective, cancellationToken: cancellationToken);

        return await _repo.GetPositionAsync(actor.OrganizationId, id, cancellationToken);
    }

    public async Task<OrgPositionDetailDto?> UnassignPositionAsync(HrActor actor, Guid id, UnassignOrgPositionRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OrganizationPermissions.AssignEmployees);

        if (request.EmployeeId == Guid.Empty && !request.AssignmentId.HasValue)
            throw EmployeeServiceException.Validation("employeeId or assignmentId is required.");

        var position = await _repo.GetPositionAsync(actor.OrganizationId, id, cancellationToken);
        if (position == null) return null;

        var endDate = (request.EndDate ?? Today()).Date;
        var ended = await _repo.UnassignPositionAsync(
            actor.OrganizationId, id, request.AssignmentId, request.EmployeeId == Guid.Empty ? null : request.EmployeeId,
            endDate, actor.UserId, cancellationToken);
        if (!ended) throw EmployeeServiceException.NotFound("No active assignment was found for that employee on this position.");

        var employeeName = request.EmployeeId == Guid.Empty
            ? "Employee"
            : await GetEmployeeNameAsync(actor.OrganizationId, request.EmployeeId, cancellationToken);
        await LogAsync(actor, "PositionUnassigned", "Position", id, $"{employeeName} unassigned from {position.Title}",
            departmentId: position.DepartmentId, employeeId: request.EmployeeId == Guid.Empty ? null : request.EmployeeId, positionId: id,
            current: new { request.AssignmentId, request.EmployeeId, request.Reason },
            effectiveDate: endDate, cancellationToken: cancellationToken);

        return await _repo.GetPositionAsync(actor.OrganizationId, id, cancellationToken);
    }

    public async Task<bool> ArchivePositionAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OrganizationPermissions.ManagePositions);

        var existing = await _repo.GetPositionAsync(actor.OrganizationId, id, cancellationToken);
        if (existing == null || !existing.IsActive) return false;

        if (existing.FilledCount > 0)
            throw EmployeeServiceException.Validation(
                $"{existing.Title} still has {existing.FilledCount} assigned employee(s). Unassign them before archiving the position.");

        if (!await _repo.ArchivePositionAsync(actor.OrganizationId, id, actor.UserId, cancellationToken))
            return false;

        await LogAsync(actor, "PositionArchived", "Position", id, $"Position archived: {existing.Title}",
            departmentId: existing.DepartmentId, positionId: id, previous: new { existing.Title }, cancellationToken: cancellationToken);
        return true;
    }

    public async Task<OrgPositionDetailDto?> MovePositionAsync(HrActor actor, Guid id, MoveOrgPositionRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OrganizationPermissions.ManagePositions);

        var existing = await _repo.GetPositionAsync(actor.OrganizationId, id, cancellationToken);
        if (existing == null || !existing.IsActive) return null;

        if (request.NewDepartmentId.HasValue
            && !await _repo.DepartmentExistsAsync(actor.OrganizationId, request.NewDepartmentId.Value, cancellationToken))
            throw EmployeeServiceException.NotFound("The destination department was not found.");

        if (request.NewReportsToPositionId.HasValue)
        {
            if (!await _repo.PositionExistsAsync(actor.OrganizationId, request.NewReportsToPositionId.Value, cancellationToken))
                throw EmployeeServiceException.NotFound("The reporting position was not found.");
            if (await _repo.WouldCreatePositionCycleAsync(actor.OrganizationId, id, request.NewReportsToPositionId, cancellationToken))
                throw EmployeeServiceException.Validation("A position cannot report to itself or to one of its own reports.");
        }

        if (!await _repo.MovePositionAsync(actor.OrganizationId, id, request.NewDepartmentId, request.NewReportsToPositionId, actor.UserId, cancellationToken))
            return null;

        var moved = await _repo.GetPositionAsync(actor.OrganizationId, id, cancellationToken);
        await LogAsync(actor, "PositionMoved", "Position", id, $"Position moved: {existing.Title}",
            departmentId: moved?.DepartmentId ?? existing.DepartmentId, positionId: id,
            previous: new { existing.DepartmentId, existing.ReportsToPositionId },
            current: new { moved?.DepartmentId, moved?.ReportsToPositionId },
            effectiveDate: request.EffectiveDate, cancellationToken: cancellationToken);
        return moved;
    }

    private async Task<OrgPositionInsert> BuildPositionRowAsync(
        Guid organizationId, CreateOrgPositionRequest request, OrgPositionDetailDto? existing, CancellationToken cancellationToken)
    {
        var title = request.Title?.Trim();
        if (string.IsNullOrEmpty(title)) throw EmployeeServiceException.Validation("Title is required.");
        if (title.Length > 200) throw EmployeeServiceException.Validation("Title must be 200 characters or fewer.");
        if (request.Code is { Length: > 50 }) throw EmployeeServiceException.Validation("Code must be 50 characters or fewer.");
        if (request.Description is { Length: > 1000 }) throw EmployeeServiceException.Validation("Description must be 1000 characters or fewer.");
        if (request.DepartmentId == Guid.Empty) throw EmployeeServiceException.Validation("departmentId is required.");

        string status;
        if (string.IsNullOrWhiteSpace(request.Status))
            status = existing?.Status ?? OrganizationPositionStatuses.Active;
        else
        {
            status = OrganizationValidation.NormalizePositionStatus(request.Status)
                     ?? throw EmployeeServiceException.Validation($"Status must be one of: {string.Join(", ", OrganizationPositionStatuses.All)}.");
        }
        if (status == OrganizationPositionStatuses.Archived)
            throw EmployeeServiceException.Validation("Use the archive action to archive a position.");

        if (!await _repo.DepartmentExistsAsync(organizationId, request.DepartmentId, cancellationToken))
            throw EmployeeServiceException.NotFound("Department not found.");

        if (request.ReportsToPositionId.HasValue)
        {
            if (existing != null && request.ReportsToPositionId == existing.Id)
                throw EmployeeServiceException.Validation("A position cannot report to itself.");
            if (!await _repo.PositionExistsAsync(organizationId, request.ReportsToPositionId.Value, cancellationToken))
                throw EmployeeServiceException.NotFound("The reporting position was not found.");
        }

        if (request.ResolvedLevelId.HasValue)
        {
            var levels = await _repo.ListLevelsAsync(organizationId, cancellationToken);
            if (levels.All(l => l.Id != request.ResolvedLevelId.Value))
                throw EmployeeServiceException.NotFound("Organizational level not found.");
        }

        if (request.ResolvedLocationId.HasValue)
        {
            var locations = await _repo.ListLocationsAsync(organizationId, cancellationToken);
            if (locations.All(l => l.Id != request.ResolvedLocationId.Value))
                throw EmployeeServiceException.NotFound("Work location not found.");
        }

        if (request.JobTitleId.HasValue)
        {
            var titles = await _employees.ListJobTitlesAsync(organizationId, cancellationToken);
            if (titles.All(t => t.Id != request.JobTitleId.Value))
                throw EmployeeServiceException.NotFound("Job title not found.");
        }

        return new OrgPositionInsert(
            title,
            request.Code,
            request.JobTitleId,
            request.DepartmentId,
            request.ReportsToPositionId,
            request.ResolvedLocationId,
            request.ResolvedLevelId,
            request.EmploymentTypeId,
            request.ResolvedHeadcount,
            status,
            request.EffectiveDate,
            request.Description);
    }

    private async Task<string> GetEmployeeNameAsync(Guid organizationId, Guid employeeId, CancellationToken cancellationToken)
    {
        var employee = await _employees.GetByIdAsync(organizationId, employeeId, cancellationToken);
        return string.IsNullOrWhiteSpace(employee?.DisplayName) ? "Employee" : employee.DisplayName;
    }

    // -------------------------------------------------------------------------
    // Levels / locations
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<OrgLevelDto>> ListLevelsAsync(HrActor actor, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OrganizationPermissions.View);
        await EnsureOrgReadyAsync(actor, cancellationToken);
        return await _repo.ListLevelsAsync(actor.OrganizationId, cancellationToken);
    }

    public async Task<IReadOnlyList<OrgLevelDto>> UpsertLevelAsync(HrActor actor, CreateOrgLevelRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OrganizationPermissions.ManageLevels);

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name)) throw EmployeeServiceException.Validation("Name is required.");
        if (name.Length > 120) throw EmployeeServiceException.Validation("Name must be 120 characters or fewer.");
        if (request.Description is { Length: > 500 }) throw EmployeeServiceException.Validation("Description must be 500 characters or fewer.");
        if (request.LevelOrder < 0) throw EmployeeServiceException.Validation("Level order must be zero or greater.");

        var id = await _repo.UpsertLevelAsync(actor.OrganizationId, name, request.Description, request.LevelOrder, actor.UserId, cancellationToken);
        await LogAsync(actor, "LevelSaved", "Level", id, $"Organizational level saved: {name}",
            current: new { name, request.LevelOrder }, cancellationToken: cancellationToken);

        return await _repo.ListLevelsAsync(actor.OrganizationId, cancellationToken);
    }

    public async Task<IReadOnlyList<OrgLocationListItemDto>> ListLocationsAsync(HrActor actor, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OrganizationPermissions.View);
        await EnsureOrgReadyAsync(actor, cancellationToken);
        return await _repo.ListLocationsAsync(actor.OrganizationId, cancellationToken);
    }

    public async Task<OrgLocationListItemDto> CreateLocationAsync(HrActor actor, CreateOrgLocationRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OrganizationPermissions.ManageLocations);
        ValidateLocation(request);

        var id = await _repo.UpsertLocationAsync(actor.OrganizationId, request, actor.UserId, cancellationToken);
        await LogAsync(actor, "LocationSaved", "Location", id, $"Location saved: {request.Name.Trim()}",
            current: new { Name = request.Name.Trim(), request.City, request.Country }, cancellationToken: cancellationToken);

        var locations = await _repo.ListLocationsAsync(actor.OrganizationId, cancellationToken);
        return locations.FirstOrDefault(l => l.Id == id)
               ?? throw EmployeeServiceException.NotFound("Location was saved but could not be loaded.");
    }

    public async Task<OrgLocationListItemDto?> UpdateLocationAsync(HrActor actor, Guid id, UpdateOrgLocationRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OrganizationPermissions.ManageLocations);
        ValidateLocation(request);

        if (!await _repo.UpdateLocationAsync(actor.OrganizationId, id, request, actor.UserId, cancellationToken))
            return null;

        await LogAsync(actor, "LocationUpdated", "Location", id, $"Location updated: {request.Name.Trim()}",
            current: new { Name = request.Name.Trim(), request.City, request.Country }, cancellationToken: cancellationToken);

        var locations = await _repo.ListLocationsAsync(actor.OrganizationId, cancellationToken);
        return locations.FirstOrDefault(l => l.Id == id);
    }

    private static void ValidateLocation(CreateOrgLocationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw EmployeeServiceException.Validation("Name is required.");
        if (request.Name.Trim().Length > 200) throw EmployeeServiceException.Validation("Name must be 200 characters or fewer.");
        if (!string.IsNullOrWhiteSpace(request.LocationType) && OrganizationValidation.NormalizeLocationType(request.LocationType) == null)
            throw EmployeeServiceException.Validation($"Location type must be one of: {string.Join(", ", OrganizationLocationTypes.All)}.");
    }

    // -------------------------------------------------------------------------
    // Reporting
    // -------------------------------------------------------------------------

    public async Task<OrgReportingOverviewDto> GetReportingOverviewAsync(HrActor actor, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OrganizationPermissions.ViewPeople);
        await EnsureOrgReadyAsync(actor, cancellationToken);
        return await _repo.GetReportingOverviewAsync(actor.OrganizationId, cancellationToken);
    }

    public async Task ChangeManagerAsync(HrActor actor, ChangeManagerRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OrganizationPermissions.ManageReporting);

        if (request.EmployeeId == Guid.Empty) throw EmployeeServiceException.Validation("employeeId is required.");

        var employee = await _employees.GetByIdAsync(actor.OrganizationId, request.EmployeeId, cancellationToken)
                       ?? throw EmployeeServiceException.NotFound("Employee not found.");

        var newManagerId = request.ResolvedManagerId;
        string? managerName = null;
        if (newManagerId.HasValue)
        {
            if (newManagerId.Value == request.EmployeeId)
                throw EmployeeServiceException.Validation("An employee cannot report to themselves.");

            var manager = await _employees.GetByIdAsync(actor.OrganizationId, newManagerId.Value, cancellationToken)
                          ?? throw EmployeeServiceException.NotFound("Manager not found.");
            managerName = manager.DisplayName;

            if (await _employees.WouldCreateManagerCycleAsync(actor.OrganizationId, request.EmployeeId, newManagerId.Value, cancellationToken))
                throw EmployeeServiceException.Validation("That change would create a circular reporting line.");
        }

        if (employee.ManagerId == newManagerId) return;

        await _repo.SetEmployeeManagerAsync(actor.OrganizationId, request.EmployeeId, newManagerId, actor.UserId, cancellationToken);

        var summary = newManagerId.HasValue
            ? $"{employee.DisplayName} now reports to {managerName}"
            : $"{employee.DisplayName} no longer has a manager";

        await LogAsync(actor, "ManagerChanged", "Employee", request.EmployeeId, summary,
            departmentId: employee.DepartmentId, employeeId: request.EmployeeId,
            previous: new { ManagerId = employee.ManagerId, employee.ManagerName },
            current: new { ManagerId = newManagerId, ManagerName = managerName, request.Reason },
            effectiveDate: request.EffectiveDate, cancellationToken: cancellationToken);

        try
        {
            await _employees.AddActivityAsync(actor.OrganizationId, request.EmployeeId, "ManagerChanged", summary,
                JsonSerializer.Serialize(new { previousManagerId = employee.ManagerId, newManagerId, request.Reason }),
                actor.UserId, actor.Name, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to record employee activity for manager change on {EmployeeId}.", request.EmployeeId);
        }
    }

    // -------------------------------------------------------------------------
    // Activity
    // -------------------------------------------------------------------------

    public async Task<PagedResult<OrgActivityDto>> ListActivityAsync(HrActor actor, OrgActivityQuery query, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, OrganizationPermissions.ViewHistory);

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        query.Page = page;
        query.PageSize = pageSize;

        var items = await _repo.ListActivityAsync(actor.OrganizationId, query, cancellationToken);
        var total = await _repo.CountActivityAsync(actor.OrganizationId, query, cancellationToken);

        return new PagedResult<OrgActivityDto> { Items = items, Page = page, PageSize = pageSize, Total = total };
    }
}
