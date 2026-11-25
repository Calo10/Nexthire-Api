using nexthire_api.DTOs;
using nexthire_api.DTOs.CoreHr;

namespace nexthire_api.Repositories;

public sealed record OrgDepartmentInsert(
    string Name,
    string? Code,
    string? Description,
    Guid? ParentDepartmentId,
    Guid? HeadEmployeeId,
    string? CostCenter,
    string? Color,
    string Icon,
    int DisplayOrder);

public sealed record OrgPositionInsert(
    string PositionName,
    string? PositionCode,
    Guid? JobTitleId,
    Guid DepartmentId,
    Guid? ReportsToPositionId,
    Guid? WorkLocationId,
    Guid? OrganizationalLevelId,
    Guid? EmploymentTypeId,
    int ApprovedHeadcount,
    string Status,
    DateTime? EffectiveDate,
    string? Description);

public interface IOrganizationRepository
{
    Task EnsureSchemaAsync();

    Task EnsureOrgLevelsAsync(Guid organizationId, Guid? actorUserId, CancellationToken cancellationToken = default);

    Task SeedDemoOrganizationIfEmptyAsync(
        Guid organizationId,
        Guid? actorUserId,
        bool isDevelopment,
        CancellationToken cancellationToken = default);

    // Summary / chart
    Task<OrganizationSummaryDto> GetSummaryAsync(Guid organizationId, DateTime today, CancellationToken cancellationToken = default);
    Task<OrgDepartmentChartDto> GetDepartmentChartAsync(Guid organizationId, string? organizationName, CancellationToken cancellationToken = default);
    Task<OrgPeopleChartDto> GetPeopleChartAsync(Guid organizationId, string? organizationName, Guid? rootEmployeeId, int? maxDepth, CancellationToken cancellationToken = default);
    Task<OrgPositionChartDto> GetPositionChartAsync(Guid organizationId, string? organizationName, Guid? departmentId, CancellationToken cancellationToken = default);

    // Departments
    Task<IReadOnlyList<OrgDepartmentListItemDto>> ListDepartmentsAsync(Guid organizationId, bool includeArchived, CancellationToken cancellationToken = default);
    Task<OrgDepartmentDetailDto?> GetDepartmentAsync(Guid organizationId, Guid id, CancellationToken cancellationToken = default);
    Task<OrgDepartmentSummaryDto?> GetDepartmentSummaryAsync(Guid organizationId, Guid id, DateTime today, CancellationToken cancellationToken = default);
    Task<Guid> InsertDepartmentAsync(Guid organizationId, OrgDepartmentInsert row, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<bool> UpdateDepartmentAsync(Guid organizationId, Guid id, OrgDepartmentInsert row, bool? isActive, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<bool> MoveDepartmentAsync(Guid organizationId, Guid id, Guid? newParentId, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<bool> ArchiveDepartmentAsync(Guid organizationId, Guid id, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<bool> WouldCreateDepartmentCycleAsync(Guid organizationId, Guid departmentId, Guid? newParentId, CancellationToken cancellationToken = default);
    Task<int> CountActiveEmployeesInDepartmentAsync(Guid organizationId, Guid departmentId, CancellationToken cancellationToken = default);
    Task<int> CountActivePositionsInDepartmentAsync(Guid organizationId, Guid departmentId, CancellationToken cancellationToken = default);

    // Positions
    Task<PagedResult<OrgPositionListItemDto>> ListPositionsAsync(Guid organizationId, OrgPositionListQuery query, CancellationToken cancellationToken = default);
    Task<OrgPositionDetailDto?> GetPositionAsync(Guid organizationId, Guid id, CancellationToken cancellationToken = default);
    Task<Guid> InsertPositionAsync(Guid organizationId, OrgPositionInsert row, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<bool> UpdatePositionAsync(Guid organizationId, Guid id, OrgPositionInsert row, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<bool> ArchivePositionAsync(Guid organizationId, Guid id, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<bool> MovePositionAsync(Guid organizationId, Guid id, Guid? newDepartmentId, Guid? newReportsTo, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<bool> WouldCreatePositionCycleAsync(Guid organizationId, Guid positionId, Guid? newReportsTo, CancellationToken cancellationToken = default);
    Task<Guid> AssignPositionAsync(Guid organizationId, Guid positionId, Guid employeeId, decimal allocationPct, bool isPrimary, DateTime effectiveDate, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<bool> UnassignPositionAsync(Guid organizationId, Guid positionId, Guid? assignmentId, Guid? employeeId, DateTime endDate, Guid? actorUserId, CancellationToken cancellationToken = default);

    // Levels / locations
    Task<IReadOnlyList<OrgLevelDto>> ListLevelsAsync(Guid organizationId, CancellationToken cancellationToken = default);
    Task<Guid> UpsertLevelAsync(Guid organizationId, string name, string? description, int levelOrder, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OrgLocationListItemDto>> ListLocationsAsync(Guid organizationId, CancellationToken cancellationToken = default);
    Task<Guid> UpsertLocationAsync(Guid organizationId, CreateOrgLocationRequest request, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<bool> UpdateLocationAsync(Guid organizationId, Guid id, UpdateOrgLocationRequest request, Guid? actorUserId, CancellationToken cancellationToken = default);

    // Reporting
    Task<OrgReportingOverviewDto> GetReportingOverviewAsync(Guid organizationId, CancellationToken cancellationToken = default);
    Task SetEmployeeManagerAsync(Guid organizationId, Guid employeeId, Guid? managerId, Guid? actorUserId, CancellationToken cancellationToken = default);

    // Activity
    Task InsertActivityAsync(
        Guid organizationId,
        string changeType,
        string entityType,
        Guid? entityId,
        Guid? departmentId,
        Guid? employeeId,
        Guid? positionId,
        string summary,
        string? previousValue,
        string? newValue,
        DateTime? effectiveDate,
        Guid? actorUserId,
        string? actorName,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OrgActivityDto>> ListActivityAsync(Guid organizationId, OrgActivityQuery query, CancellationToken cancellationToken = default);
    Task<int> CountActivityAsync(Guid organizationId, OrgActivityQuery query, CancellationToken cancellationToken = default);

    // Lookups used by validation
    Task<bool> DepartmentExistsAsync(Guid organizationId, Guid id, CancellationToken cancellationToken = default);
    Task<bool> EmployeeExistsAsync(Guid organizationId, Guid id, CancellationToken cancellationToken = default);
    Task<bool> PositionExistsAsync(Guid organizationId, Guid id, CancellationToken cancellationToken = default);
}
