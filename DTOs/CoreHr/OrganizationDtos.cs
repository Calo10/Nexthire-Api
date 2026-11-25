namespace nexthire_api.DTOs.CoreHr;

// ---------------------------------------------------------------------------
// Summary / analytics
// ---------------------------------------------------------------------------

public sealed class OrganizationSummaryDto
{
    public int TotalEmployees { get; set; }
    public double EmployeeGrowthPct { get; set; }
    public int TotalDepartments { get; set; }
    public int Teams { get; set; }
    public int TotalPositions { get; set; }
    public int OccupiedPositions { get; set; }
    public int OpenPositions { get; set; }
    public int VacantHeadcount { get; set; }
    public int ApprovedHeadcount { get; set; }
    public int ActualHeadcount { get; set; }
    public double HeadcountUtilizationPct { get; set; }
    public double AverageSpanOfControl { get; set; }
    public int TotalLocations { get; set; }
    public int EmployeesWithoutManager { get; set; }
    public int ManagersWithoutReports { get; set; }
    public int EmployeesWithoutPosition { get; set; }
    public int MaxDepth { get; set; }
}

// ---------------------------------------------------------------------------
// Chart
// ---------------------------------------------------------------------------

public sealed class OrgChartDepartmentNodeDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Code { get; set; }
    public string? Description { get; set; }
    public Guid? ParentDepartmentId { get; set; }
    public string? Color { get; set; }
    public string Icon { get; set; } = "default";
    public int DisplayOrder { get; set; }
    public Guid? HeadEmployeeId { get; set; }
    public string? HeadName { get; set; }
    public string? HeadPhotoUrl { get; set; }
    public string? HeadJobTitle { get; set; }
    public int EmployeeCount { get; set; }
    public int DirectEmployeeCount { get; set; }
    public int TeamCount { get; set; }
    public int PositionCount { get; set; }
    public int OpenPositionCount { get; set; }
    public int? BudgetedHeadcount { get; set; }
    public double? UtilizationPercent { get; set; }
    public bool IsActive { get; set; } = true;
    public IReadOnlyList<OrgChartAvatarDto> Members { get; set; } = [];
    public IReadOnlyList<OrgChartDepartmentNodeDto> Children { get; set; } = [];
}

public sealed class OrgChartPeopleNodeDto
{
    public Guid EmployeeId { get; set; }
    public string Name { get; set; } = "";
    public string? JobTitle { get; set; }
    public Guid? DepartmentId { get; set; }
    public string? DepartmentName { get; set; }
    public string? PhotoUrl { get; set; }
    public Guid? ManagerId { get; set; }
    public string EmploymentStatus { get; set; } = "";
    public int DirectReports { get; set; }
    public int TotalReports { get; set; }
    public IReadOnlyList<OrgChartPeopleNodeDto> Children { get; set; } = [];
}

public sealed class OrgChartPositionNodeDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string? Code { get; set; }
    public Guid? DepartmentId { get; set; }
    public string? DepartmentName { get; set; }
    public Guid? ReportsToPositionId { get; set; }
    public string? LevelName { get; set; }
    public string Status { get; set; } = "";
    public int Headcount { get; set; }
    public int FilledCount { get; set; }
    public int OpenCount { get; set; }
    public IReadOnlyList<OrgChartAvatarDto> Occupants { get; set; } = [];
    public IReadOnlyList<OrgChartPositionNodeDto> Children { get; set; } = [];
}

public sealed class OrgChartAvatarDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? JobTitle { get; set; }
    public string? PhotoUrl { get; set; }
}

public sealed class OrgChartCompanyRootDto
{
    public Guid? Id { get; set; }
    public string Name { get; set; } = "";
    public Guid? CeoEmployeeId { get; set; }
    public string? CeoName { get; set; }
    public string? CeoJobTitle { get; set; }
    public string? LogoUrl { get; set; }
    public int TotalEmployees { get; set; }
    public int TotalDepartments { get; set; }
    public int DirectReportCount { get; set; }
}

public sealed class OrgDepartmentChartDto
{
    public OrgChartCompanyRootDto Root { get; set; } = new();
    public IReadOnlyList<OrgChartDepartmentNodeDto> Departments { get; set; } = [];
}

public sealed class OrgPeopleChartDto
{
    public OrgChartPeopleNodeDto? Root { get; set; }
    public IReadOnlyList<OrgChartPeopleNodeDto> Roots { get; set; } = [];
    public bool Truncated { get; set; }
}

public sealed class OrgPositionChartDto
{
    public IReadOnlyList<OrgChartPositionNodeDto> Positions { get; set; } = [];
}

// ---------------------------------------------------------------------------
// Departments
// ---------------------------------------------------------------------------

public class OrgDepartmentListItemDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Code { get; set; }
    public string? Description { get; set; }
    public Guid? ParentDepartmentId { get; set; }
    public string? ParentDepartmentName { get; set; }
    public Guid? HeadEmployeeId { get; set; }
    public string? HeadName { get; set; }
    public string? HeadPhotoUrl { get; set; }
    public string? HeadJobTitle { get; set; }
    public string? CostCenter { get; set; }
    public string? Color { get; set; }
    public string Icon { get; set; } = "default";
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; }
    public int EmployeeCount { get; set; }
    public int DirectEmployeeCount { get; set; }
    public int TeamCount { get; set; }
    public int PositionCount { get; set; }
    public int OpenPositionCount { get; set; }
    public int? BudgetedHeadcount { get; set; }
    public double? UtilizationPercent { get; set; }
    public IReadOnlyList<OrgChartAvatarDto> Members { get; set; } = [];
}

public class OrgDepartmentDetailDto : OrgDepartmentListItemDto
{
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class OrgDepartmentSummaryDto
{
    public Guid DepartmentId { get; set; }
    public int EmployeeCount { get; set; }
    public int DirectEmployeeCount { get; set; }
    public int SubDepartmentCount { get; set; }
    public int PositionCount { get; set; }
    public int OpenPositionCount { get; set; }
    public int FilledPositionCount { get; set; }
    public int? BudgetedHeadcount { get; set; }
    public double? UtilizationPercent { get; set; }
    public double? AverageSpanOfControl { get; set; }
    public int RecentHires { get; set; }
    public int RecentExits { get; set; }
    public Guid? HeadEmployeeId { get; set; }
    public string? HeadName { get; set; }
    public string? HeadJobTitle { get; set; }
    public string? HeadPhotoUrl { get; set; }
    public string? HeadWorkEmail { get; set; }
    public string? HeadWorkPhone { get; set; }
    public string? Description { get; set; }
    public string Icon { get; set; } = "default";
    public string? Color { get; set; }
    public string Name { get; set; } = "";
}

public class CreateOrgDepartmentRequest
{
    public string Name { get; set; } = "";
    public string? Code { get; set; }
    public string? Description { get; set; }
    public Guid? ParentDepartmentId { get; set; }
    public Guid? HeadEmployeeId { get; set; }
    public string? CostCenter { get; set; }
    public string? Color { get; set; }
    public string? Icon { get; set; }
    public int? DisplayOrder { get; set; }
}

public sealed class UpdateOrgDepartmentRequest : CreateOrgDepartmentRequest
{
    public bool? IsActive { get; set; }
}

public sealed class MoveOrgDepartmentRequest
{
    public Guid? NewParentDepartmentId { get; set; }
    public DateTime? EffectiveDate { get; set; }
}

// ---------------------------------------------------------------------------
// Positions
// ---------------------------------------------------------------------------

public class OrgPositionListItemDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string? Code { get; set; }
    public Guid? JobTitleId { get; set; }
    public string? JobTitle { get; set; }
    public Guid DepartmentId { get; set; }
    public string? DepartmentName { get; set; }
    public Guid? ReportsToPositionId { get; set; }
    public string? ReportsToPositionName { get; set; }
    public Guid? WorkLocationId { get; set; }
    public string? LocationName { get; set; }
    public Guid? OrganizationalLevelId { get; set; }
    public Guid? LevelId { get; set; }
    public string? LevelName { get; set; }
    public string Status { get; set; } = "";
    public int Headcount { get; set; }
    public int FilledCount { get; set; }
    public int OpenCount { get; set; }
    public bool IsActive { get; set; } = true;
    public IReadOnlyList<OrgChartAvatarDto> Occupants { get; set; } = [];
    public DateTime? EffectiveDate { get; set; }
}

public sealed class OrgPositionDetailDto : OrgPositionListItemDto
{
    public string? Description { get; set; }
    public Guid? EmploymentTypeId { get; set; }
    public string? EmploymentType { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class OrgPositionListQuery
{
    public Guid? DepartmentId { get; set; }
    public Guid? JobTitleId { get; set; }
    public Guid? LevelId { get; set; }
    public Guid? OrganizationalLevelId { get; set; }
    public Guid? LocationId { get; set; }
    public Guid? WorkLocationId { get; set; }
    public string? Status { get; set; }
    public string? Occupancy { get; set; }
    public string? Search { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;

    public Guid? ResolvedLevelId => LevelId ?? OrganizationalLevelId;
    public Guid? ResolvedLocationId => LocationId ?? WorkLocationId;
}

public class CreateOrgPositionRequest
{
    public string Title { get; set; } = "";
    public string? Code { get; set; }
    public Guid? JobTitleId { get; set; }
    public Guid DepartmentId { get; set; }
    public Guid? ReportsToPositionId { get; set; }
    public Guid? LocationId { get; set; }
    public Guid? WorkLocationId { get; set; }
    public Guid? LevelId { get; set; }
    public Guid? OrganizationalLevelId { get; set; }
    public Guid? EmploymentTypeId { get; set; }
    public int? Headcount { get; set; }
    public int ApprovedHeadcount { get; set; } = 1;
    public string? Status { get; set; }
    public DateTime? EffectiveDate { get; set; }
    public string? Description { get; set; }

    public Guid? ResolvedLocationId => LocationId ?? WorkLocationId;
    public Guid? ResolvedLevelId => LevelId ?? OrganizationalLevelId;
    public int ResolvedHeadcount => Headcount is > 0 ? Headcount.Value : (ApprovedHeadcount > 0 ? ApprovedHeadcount : 1);
}

public sealed class UpdateOrgPositionRequest : CreateOrgPositionRequest { }

public sealed class AssignOrgPositionRequest
{
    public Guid EmployeeId { get; set; }
    public decimal AllocationPct { get; set; } = 100;
    public bool IsPrimary { get; set; } = true;
    public DateTime? EffectiveDate { get; set; }
}

public sealed class UnassignOrgPositionRequest
{
    public Guid? AssignmentId { get; set; }
    public Guid EmployeeId { get; set; }
    public DateTime? EndDate { get; set; }
    public string? Reason { get; set; }
}

public sealed class MoveOrgPositionRequest
{
    public Guid? NewDepartmentId { get; set; }
    public Guid? NewReportsToPositionId { get; set; }
    public DateTime? EffectiveDate { get; set; }
}

// ---------------------------------------------------------------------------
// Levels / locations / reporting / activity
// ---------------------------------------------------------------------------

public sealed class OrgLevelDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int Rank { get; set; }
    public int LevelOrder { get; set; }
    public int PositionCount { get; set; }
    public bool IsActive { get; set; }
}

public sealed class CreateOrgLevelRequest
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int LevelOrder { get; set; }
}

public sealed class OrgLocationListItemDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Country { get; set; }
    public string? AddressLine { get; set; }
    public string? Timezone { get; set; }
    public string LocationType { get; set; } = "Office";
    public bool IsRemote { get; set; }
    public bool IsHeadquarters { get; set; }
    public bool IsActive { get; set; }
    public int EmployeeCount { get; set; }
    public int DepartmentCount { get; set; }
}

public class CreateOrgLocationRequest
{
    public string Name { get; set; } = "";
    public string? Code { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Country { get; set; }
    public string? AddressLine { get; set; }
    public string? AddressLine1 { get; set; }
    public string? Timezone { get; set; }
    public string? LocationType { get; set; }
    public bool? IsHeadquarters { get; set; }
    public bool? IsRemote { get; set; }

    public string? ResolvedAddress => AddressLine ?? AddressLine1;
}

public sealed class UpdateOrgLocationRequest : CreateOrgLocationRequest
{
    public bool? IsActive { get; set; }
}

public sealed class OrgReportingOverviewDto
{
    public double AverageSpanOfControl { get; set; }
    public int MaxSpanOfControl { get; set; }
    public int MaxDepth { get; set; }
    public int ManagerCount { get; set; }
    public int OrphanCount { get; set; }
    public IReadOnlyList<OrgChartPeopleNodeDto> Orphans { get; set; } = [];
    public IReadOnlyList<OrgChartPeopleNodeDto> Managers { get; set; } = [];
}

public sealed class ChangeManagerRequest
{
    public Guid EmployeeId { get; set; }
    public Guid? NewManagerId { get; set; }
    public Guid? ManagerId { get; set; }
    public DateTime? EffectiveDate { get; set; }
    public string? Reason { get; set; }

    public Guid? ResolvedManagerId => NewManagerId ?? ManagerId;
}

public sealed class OrgActivityDto
{
    public Guid Id { get; set; }
    public string Action { get; set; } = "";
    public string? EntityType { get; set; }
    public Guid? EntityId { get; set; }
    public string? EntityName { get; set; }
    public string? Description { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? EmployeeId { get; set; }
    public Guid? PositionId { get; set; }
    public DateTime? EffectiveDate { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? ActorName { get; set; }
}

public sealed class OrgActivityQuery
{
    public Guid? DepartmentId { get; set; }
    public Guid? EmployeeId { get; set; }
    public Guid? PositionId { get; set; }
    public Guid? EntityId { get; set; }
    public string? EntityType { get; set; }
    public string? ChangeType { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public int Take { get; set; } = 50;
}
