using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs.CoreHr;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

/// <summary>Core HR report dashboards (widgets over saved reports). Requires Reports.View to read, Reports.ManageDashboards to change.</summary>
[ApiController]
[Route("api/hr/report-dashboards")]
[Authorize]
public class HrReportDashboardsController : CoreHrControllerBase
{
    private readonly IHrReportsService _reports;

    public HrReportDashboardsController(IHrReportsService reports, ILogger<HrReportDashboardsController> logger)
        : base(logger)
    {
        _reports = reports;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ReportDashboardDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> List(CancellationToken ct) =>
        RunAsync(async a => Ok(await _reports.ListDashboardsAsync(a, ct)), nameof(List));

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ReportDashboardDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var dashboard = await _reports.GetDashboardAsync(a, id, ct);
            return dashboard == null ? NotFoundResult("Dashboard") : Ok(dashboard);
        }, nameof(Get));

    [HttpPost]
    [ProducesResponseType(typeof(ReportDashboardDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Create([FromBody] UpsertReportDashboardRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var created = await _reports.CreateDashboardAsync(a, request, ct);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }, nameof(Create));

    /// <summary>Updates the dashboard. When <c>widgets</c> is present the whole widget set is replaced.</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(ReportDashboardDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Update(Guid id, [FromBody] UpsertReportDashboardRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var updated = await _reports.UpdateDashboardAsync(a, id, request, ct);
            return updated == null ? NotFoundResult("Dashboard") : Ok(updated);
        }, nameof(Update));

    /// <summary>Soft-archives the dashboard.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
        RunAsync(async a => await _reports.ArchiveDashboardAsync(a, id, ct) ? NoContent() : NotFoundResult("Dashboard"), nameof(Delete));

    [HttpPost("{id:guid}/archive")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Archive(Guid id, CancellationToken ct) =>
        RunAsync(async a => await _reports.ArchiveDashboardAsync(a, id, ct) ? NoContent() : NotFoundResult("Dashboard"), nameof(Archive));
}
