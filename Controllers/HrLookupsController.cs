using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs.CoreHr;
using nexthire_api.Exceptions;
using nexthire_api.Security;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

/// <summary>Core HR lookup lists: departments, job titles, work locations and employment types.</summary>
[ApiController]
[Route("api/hr/lookups")]
[Authorize]
public class HrLookupsController : ControllerBase
{
    private readonly IEmployeeService _employees;
    private readonly ILogger<HrLookupsController> _logger;

    public HrLookupsController(IEmployeeService employees, ILogger<HrLookupsController> logger)
    {
        _employees = employees;
        _logger = logger;
    }

    private HrActor Actor() => new(
        ClaimUtils.RequireOrgId(User),
        ClaimUtils.RequireNexaUserId(User),
        ClaimUtils.GetDisplayName(User) ?? ClaimUtils.GetEmail(User));

    private async Task<IActionResult> RunAsync(Func<HrActor, Task<IActionResult>> action, string operation)
    {
        try
        {
            return await action(Actor());
        }
        catch (HrAccessDeniedException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message, code = "forbidden" });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (EmployeeServiceException ex)
        {
            return StatusCode(ex.StatusCode, new { message = ex.Message, code = ex.Code });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during HR lookups operation: {Operation}", operation);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while processing the HR lookup request." });
        }
    }

    [HttpGet("departments")]
    [ProducesResponseType(typeof(IReadOnlyList<HrLookupDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> ListDepartments(CancellationToken ct) =>
        RunAsync(async a => Ok(await _employees.ListDepartmentsAsync(a, ct)), nameof(ListDepartments));

    /// <summary>Creates a department, or reactivates/updates it when the name already exists.</summary>
    [HttpPost("departments")]
    [ProducesResponseType(typeof(HrLookupDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> UpsertDepartment([FromBody] CreateHrLookupRequest request, CancellationToken ct) =>
        RunAsync(async a => Created("/api/hr/lookups/departments", await _employees.UpsertDepartmentAsync(a, request, ct)), nameof(UpsertDepartment));

    [HttpGet("job-titles")]
    [ProducesResponseType(typeof(IReadOnlyList<HrLookupDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> ListJobTitles(CancellationToken ct) =>
        RunAsync(async a => Ok(await _employees.ListJobTitlesAsync(a, ct)), nameof(ListJobTitles));

    [HttpPost("job-titles")]
    [ProducesResponseType(typeof(HrLookupDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> UpsertJobTitle([FromBody] CreateHrLookupRequest request, CancellationToken ct) =>
        RunAsync(async a => Created("/api/hr/lookups/job-titles", await _employees.UpsertJobTitleAsync(a, request, ct)), nameof(UpsertJobTitle));

    [HttpGet("locations")]
    [ProducesResponseType(typeof(IReadOnlyList<WorkLocationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> ListLocations(CancellationToken ct) =>
        RunAsync(async a => Ok(await _employees.ListLocationsAsync(a, ct)), nameof(ListLocations));

    [HttpPost("locations")]
    [ProducesResponseType(typeof(WorkLocationDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> UpsertLocation([FromBody] CreateHrLookupRequest request, CancellationToken ct) =>
        RunAsync(async a => Created("/api/hr/lookups/locations", await _employees.UpsertLocationAsync(a, request, ct)), nameof(UpsertLocation));

    /// <summary>Employment types are seeded per organization (FullTime, PartTime, Contractor, Temporary, Intern).</summary>
    [HttpGet("employment-types")]
    [ProducesResponseType(typeof(IReadOnlyList<HrLookupDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> ListEmploymentTypes(CancellationToken ct) =>
        RunAsync(async a => Ok(await _employees.ListEmploymentTypesAsync(a, ct)), nameof(ListEmploymentTypes));
}
