using nexthire_api.DTOs;
using nexthire_api.DTOs.CoreHr;

namespace nexthire_api.Services;

/// <summary>
/// Core HR Organization: departments hierarchy, positions and headcount, levels, locations, reporting lines and
/// organizational history. Every method enforces <see cref="nexthire_api.Security.OrganizationPermissions"/>
/// (<see cref="nexthire_api.Exceptions.HrAccessDeniedException"/> otherwise). Business-rule failures throw
/// <see cref="nexthire_api.Exceptions.EmployeeServiceException"/>; missing records return null / false.
/// </summary>
public interface IOrganizationService
{
    Task<IReadOnlyList<string>> GetCapabilitiesAsync(HrActor actor, CancellationToken cancellationToken = default);

    // Summary / charts
    Task<OrganizationSummaryDto> GetSummaryAsync(HrActor actor, CancellationToken cancellationToken = default);
    Task<OrgDepartmentChartDto> GetDepartmentChartAsync(HrActor actor, CancellationToken cancellationToken = default);
    Task<OrgPeopleChartDto> GetPeopleChartAsync(HrActor actor, Guid? rootEmployeeId, int? maxDepth, CancellationToken cancellationToken = default);
    Task<OrgPositionChartDto> GetPositionChartAsync(HrActor actor, Guid? departmentId, CancellationToken cancellationToken = default);

    // Departments
    Task<IReadOnlyList<OrgDepartmentListItemDto>> ListDepartmentsAsync(HrActor actor, bool includeArchived, CancellationToken cancellationToken = default);
    Task<OrgDepartmentDetailDto?> GetDepartmentAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default);
    Task<OrgDepartmentSummaryDto?> GetDepartmentSummaryAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default);
    Task<OrgDepartmentDetailDto> CreateDepartmentAsync(HrActor actor, CreateOrgDepartmentRequest request, CancellationToken cancellationToken = default);
    Task<OrgDepartmentDetailDto?> UpdateDepartmentAsync(HrActor actor, Guid id, UpdateOrgDepartmentRequest request, CancellationToken cancellationToken = default);
    Task<OrgDepartmentDetailDto?> MoveDepartmentAsync(HrActor actor, Guid id, MoveOrgDepartmentRequest request, CancellationToken cancellationToken = default);
    Task<bool> ArchiveDepartmentAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default);

    // Positions
    Task<PagedResult<OrgPositionListItemDto>> ListPositionsAsync(HrActor actor, OrgPositionListQuery query, CancellationToken cancellationToken = default);
    Task<OrgPositionDetailDto?> GetPositionAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default);
    Task<OrgPositionDetailDto> CreatePositionAsync(HrActor actor, CreateOrgPositionRequest request, CancellationToken cancellationToken = default);
    Task<OrgPositionDetailDto?> UpdatePositionAsync(HrActor actor, Guid id, UpdateOrgPositionRequest request, CancellationToken cancellationToken = default);
    Task<OrgPositionDetailDto?> AssignPositionAsync(HrActor actor, Guid id, AssignOrgPositionRequest request, CancellationToken cancellationToken = default);
    Task<OrgPositionDetailDto?> UnassignPositionAsync(HrActor actor, Guid id, UnassignOrgPositionRequest request, CancellationToken cancellationToken = default);
    Task<bool> ArchivePositionAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default);
    Task<OrgPositionDetailDto?> MovePositionAsync(HrActor actor, Guid id, MoveOrgPositionRequest request, CancellationToken cancellationToken = default);

    // Levels / locations
    Task<IReadOnlyList<OrgLevelDto>> ListLevelsAsync(HrActor actor, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OrgLevelDto>> UpsertLevelAsync(HrActor actor, CreateOrgLevelRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OrgLocationListItemDto>> ListLocationsAsync(HrActor actor, CancellationToken cancellationToken = default);
    Task<OrgLocationListItemDto> CreateLocationAsync(HrActor actor, CreateOrgLocationRequest request, CancellationToken cancellationToken = default);
    Task<OrgLocationListItemDto?> UpdateLocationAsync(HrActor actor, Guid id, UpdateOrgLocationRequest request, CancellationToken cancellationToken = default);

    // Reporting
    Task<OrgReportingOverviewDto> GetReportingOverviewAsync(HrActor actor, CancellationToken cancellationToken = default);
    Task ChangeManagerAsync(HrActor actor, ChangeManagerRequest request, CancellationToken cancellationToken = default);

    // Activity
    Task<PagedResult<OrgActivityDto>> ListActivityAsync(HrActor actor, OrgActivityQuery query, CancellationToken cancellationToken = default);
}
