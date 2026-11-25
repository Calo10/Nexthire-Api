using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs.CoreHr;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

/// <summary>
/// Core HR report schedules and run history. Requires Reports.Schedule (and Reports.Export to create);
/// history requires Reports.ViewExecutionHistory. Schedules run as their owner and only email a summary, never the data.
/// </summary>
[ApiController]
[Route("api/hr/report-schedules")]
[Authorize]
public class HrReportSchedulesController : CoreHrControllerBase
{
    private readonly IHrReportsService _reports;

    public HrReportSchedulesController(IHrReportsService reports, ILogger<HrReportSchedulesController> logger)
        : base(logger)
    {
        _reports = reports;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ReportScheduleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> List(CancellationToken ct) =>
        RunAsync(async a => Ok(await _reports.ListSchedulesAsync(a, ct)), nameof(List));

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ReportScheduleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var schedule = await _reports.GetScheduleAsync(a, id, ct);
            return schedule == null ? NotFoundResult("Schedule") : Ok(schedule);
        }, nameof(Get));

    [HttpPost]
    [ProducesResponseType(typeof(ReportScheduleDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Create([FromBody] UpsertReportScheduleRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var created = await _reports.CreateScheduleAsync(a, request, ct);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }, nameof(Create));

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(ReportScheduleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Update(Guid id, [FromBody] UpsertReportScheduleRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var updated = await _reports.UpdateScheduleAsync(a, id, request, ct);
            return updated == null ? NotFoundResult("Schedule") : Ok(updated);
        }, nameof(Update));

    /// <summary>Soft-archives (and deactivates) the schedule.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
        RunAsync(async a => await _reports.ArchiveScheduleAsync(a, id, ct) ? NoContent() : NotFoundResult("Schedule"), nameof(Delete));

    [HttpPost("{id:guid}/archive")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Archive(Guid id, CancellationToken ct) =>
        RunAsync(async a => await _reports.ArchiveScheduleAsync(a, id, ct) ? NoContent() : NotFoundResult("Schedule"), nameof(Archive));

    /// <summary>Most recent runs of the schedule (newest first).</summary>
    [HttpGet("{id:guid}/history")]
    [ProducesResponseType(typeof(IReadOnlyList<ReportExecutionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> History(Guid id, [FromQuery] int take = 25, CancellationToken ct = default) =>
        RunAsync(async a =>
        {
            var history = await _reports.GetScheduleHistoryAsync(a, id, take, ct);
            return history == null ? NotFoundResult("Schedule") : Ok(history);
        }, nameof(History));
}
