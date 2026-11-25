using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs;
using nexthire_api.DTOs.CoreHr;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

/// <summary>
/// Core HR Organization: department hierarchy, positions and headcount, levels, locations, reporting lines
/// and organizational activity. Capabilities derive from role codes (<c>OrganizationPermissions</c>).
/// </summary>
[ApiController]
[Route("api/hr/organization")]
[Authorize]
public class OrganizationController : CoreHrControllerBase
{
    private readonly IOrganizationService _organization;

    public OrganizationController(IOrganizationService organization, ILogger<OrganizationController> logger)
        : base(logger)
    {
        _organization = organization;
    }

    // -------------------------------------------------------------------------
    // Capabilities / summary / charts
    // -------------------------------------------------------------------------

    /// <summary>Current user's Organization capabilities (derived from role codes).</summary>
    [HttpGet("capabilities")]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public Task<IActionResult> GetCapabilities(CancellationToken ct) =>
        RunAsync(async a => Ok(await _organization.GetCapabilitiesAsync(a, ct)), nameof(GetCapabilities));

    /// <summary>KPIs: employees, departments, positions, headcount, span of control, locations and gaps.</summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(OrganizationSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Summary(CancellationToken ct) =>
        RunAsync(async a => Ok(await _organization.GetSummaryAsync(a, ct)), nameof(Summary));

    /// <summary>Nested department tree with heads, employee/position counts and member avatars.</summary>
    [HttpGet("chart/departments")]
    [ProducesResponseType(typeof(OrgDepartmentChartDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> DepartmentChart(CancellationToken ct) =>
        RunAsync(async a => Ok(await _organization.GetDepartmentChartAsync(a, ct)), nameof(DepartmentChart));

    /// <summary>Manager → report tree. Optionally rooted at an employee and limited to <c>maxDepth</c> levels.</summary>
    [HttpGet("chart/people")]
    [ProducesResponseType(typeof(OrgPeopleChartDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> PeopleChart([FromQuery] Guid? rootEmployeeId, [FromQuery] int? maxDepth, CancellationToken ct) =>
        RunAsync(async a => Ok(await _organization.GetPeopleChartAsync(a, rootEmployeeId, maxDepth, ct)), nameof(PeopleChart));

    /// <summary>Flat position list with occupancy; the client nests it by <c>reportsToPositionId</c>.</summary>
    [HttpGet("chart/positions")]
    [ProducesResponseType(typeof(OrgPositionChartDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> PositionChart([FromQuery] Guid? departmentId, CancellationToken ct) =>
        RunAsync(async a => Ok(await _organization.GetPositionChartAsync(a, departmentId, ct)), nameof(PositionChart));

    // -------------------------------------------------------------------------
    // Departments
    // -------------------------------------------------------------------------

    [HttpGet("departments")]
    [ProducesResponseType(typeof(IReadOnlyList<OrgDepartmentListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> ListDepartments([FromQuery] bool includeArchived = false, CancellationToken ct = default) =>
        RunAsync(async a => Ok(await _organization.ListDepartmentsAsync(a, includeArchived, ct)), nameof(ListDepartments));

    [HttpGet("departments/{id:guid}")]
    [ProducesResponseType(typeof(OrgDepartmentDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> GetDepartment(Guid id, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var dept = await _organization.GetDepartmentAsync(a, id, ct);
            return dept == null ? NotFoundResult("Department") : Ok(dept);
        }, nameof(GetDepartment));

    [HttpGet("departments/{id:guid}/summary")]
    [ProducesResponseType(typeof(OrgDepartmentSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> GetDepartmentSummary(Guid id, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var summary = await _organization.GetDepartmentSummaryAsync(a, id, ct);
            return summary == null ? NotFoundResult("Department") : Ok(summary);
        }, nameof(GetDepartmentSummary));

    [HttpPost("departments")]
    [ProducesResponseType(typeof(OrgDepartmentDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> CreateDepartment([FromBody] CreateOrgDepartmentRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var created = await _organization.CreateDepartmentAsync(a, request, ct);
            return CreatedAtAction(nameof(GetDepartment), new { id = created.Id }, created);
        }, nameof(CreateDepartment));

    [HttpPut("departments/{id:guid}")]
    [ProducesResponseType(typeof(OrgDepartmentDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> UpdateDepartment(Guid id, [FromBody] UpdateOrgDepartmentRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var updated = await _organization.UpdateDepartmentAsync(a, id, request, ct);
            return updated == null ? NotFoundResult("Department") : Ok(updated);
        }, nameof(UpdateDepartment));

    /// <summary>Moves a department under another parent (or to the top level). Rejects cycles.</summary>
    [HttpPost("departments/{id:guid}/move")]
    [ProducesResponseType(typeof(OrgDepartmentDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> MoveDepartment(Guid id, [FromBody] MoveOrgDepartmentRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var moved = await _organization.MoveDepartmentAsync(a, id, request ?? new MoveOrgDepartmentRequest(), ct);
            return moved == null ? NotFoundResult("Department") : Ok(moved);
        }, nameof(MoveDepartment));

    /// <summary>Soft-archives a department. Blocked while it still has active employees, positions or sub-departments.</summary>
    [HttpPost("departments/{id:guid}/archive")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> ArchiveDepartment(Guid id, CancellationToken ct) =>
        RunAsync(async a => await _organization.ArchiveDepartmentAsync(a, id, ct) ? NoContent() : NotFoundResult("Department"), nameof(ArchiveDepartment));

    // -------------------------------------------------------------------------
    // Positions
    // -------------------------------------------------------------------------

    [HttpGet("positions")]
    [ProducesResponseType(typeof(PagedResult<OrgPositionListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> ListPositions([FromQuery] OrgPositionListQuery query, CancellationToken ct) =>
        RunAsync(async a => Ok(await _organization.ListPositionsAsync(a, query, ct)), nameof(ListPositions));

    [HttpGet("positions/{id:guid}")]
    [ProducesResponseType(typeof(OrgPositionDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> GetPosition(Guid id, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var position = await _organization.GetPositionAsync(a, id, ct);
            return position == null ? NotFoundResult("Position") : Ok(position);
        }, nameof(GetPosition));

    [HttpPost("positions")]
    [ProducesResponseType(typeof(OrgPositionDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> CreatePosition([FromBody] CreateOrgPositionRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var created = await _organization.CreatePositionAsync(a, request, ct);
            return CreatedAtAction(nameof(GetPosition), new { id = created.Id }, created);
        }, nameof(CreatePosition));

    [HttpPut("positions/{id:guid}")]
    [ProducesResponseType(typeof(OrgPositionDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> UpdatePosition(Guid id, [FromBody] UpdateOrgPositionRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var updated = await _organization.UpdatePositionAsync(a, id, request, ct);
            return updated == null ? NotFoundResult("Position") : Ok(updated);
        }, nameof(UpdatePosition));

    /// <summary>Assigns an employee to an open seat; a primary assignment ends the employee's prior primary one.</summary>
    [HttpPost("positions/{id:guid}/assign")]
    [ProducesResponseType(typeof(OrgPositionDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> AssignPosition(Guid id, [FromBody] AssignOrgPositionRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var position = await _organization.AssignPositionAsync(a, id, request, ct);
            return position == null ? NotFoundResult("Position") : Ok(position);
        }, nameof(AssignPosition));

    [HttpPost("positions/{id:guid}/unassign")]
    [ProducesResponseType(typeof(OrgPositionDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> UnassignPosition(Guid id, [FromBody] UnassignOrgPositionRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var position = await _organization.UnassignPositionAsync(a, id, request, ct);
            return position == null ? NotFoundResult("Position") : Ok(position);
        }, nameof(UnassignPosition));

    /// <summary>Soft-archives a position. Blocked while employees are still assigned.</summary>
    [HttpPost("positions/{id:guid}/archive")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> ArchivePosition(Guid id, CancellationToken ct) =>
        RunAsync(async a => await _organization.ArchivePositionAsync(a, id, ct) ? NoContent() : NotFoundResult("Position"), nameof(ArchivePosition));

    // -------------------------------------------------------------------------
    // Levels / locations
    // -------------------------------------------------------------------------

    [HttpGet("levels")]
    [ProducesResponseType(typeof(IReadOnlyList<OrgLevelDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> ListLevels(CancellationToken ct) =>
        RunAsync(async a => Ok(await _organization.ListLevelsAsync(a, ct)), nameof(ListLevels));

    [HttpGet("locations")]
    [ProducesResponseType(typeof(IReadOnlyList<OrgLocationListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> ListLocations(CancellationToken ct) =>
        RunAsync(async a => Ok(await _organization.ListLocationsAsync(a, ct)), nameof(ListLocations));

    [HttpPost("locations")]
    [ProducesResponseType(typeof(OrgLocationListItemDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> CreateLocation([FromBody] CreateOrgLocationRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var created = await _organization.CreateLocationAsync(a, request, ct);
            return Created("/api/hr/organization/locations", created);
        }, nameof(CreateLocation));

    [HttpPut("locations/{id:guid}")]
    [ProducesResponseType(typeof(OrgLocationListItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> UpdateLocation(Guid id, [FromBody] UpdateOrgLocationRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var updated = await _organization.UpdateLocationAsync(a, id, request, ct);
            return updated == null ? NotFoundResult("Location") : Ok(updated);
        }, nameof(UpdateLocation));

    // -------------------------------------------------------------------------
    // Reporting / activity
    // -------------------------------------------------------------------------

    /// <summary>Managers with direct-report counts and employees outside any reporting line.</summary>
    [HttpGet("reporting")]
    [ProducesResponseType(typeof(OrgReportingOverviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Reporting(CancellationToken ct) =>
        RunAsync(async a => Ok(await _organization.GetReportingOverviewAsync(a, ct)), nameof(Reporting));

    /// <summary>Changes an employee's manager. Rejects circular reporting lines.</summary>
    [HttpPost("reporting/change-manager")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> ChangeManager([FromBody] ChangeManagerRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            await _organization.ChangeManagerAsync(a, request, ct);
            return NoContent();
        }, nameof(ChangeManager));

    [HttpGet("activity")]
    [ProducesResponseType(typeof(PagedResult<OrgActivityDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Activity([FromQuery] OrgActivityQuery query, CancellationToken ct) =>
        RunAsync(async a => Ok(await _organization.ListActivityAsync(a, query, ct)), nameof(Activity));
}
