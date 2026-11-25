using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs.CoreHr;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

/// <summary>
/// Core HR report executions. Callers see their own executions; Reports.ViewExecutionHistory shows everyone's.
/// Stored outputs download through <c>GET /api/documents/{documentId}/download-url</c> (or <c>/download</c> here).
/// </summary>
[ApiController]
[Route("api/hr/report-executions")]
[Authorize]
public class HrReportExecutionsController : CoreHrControllerBase
{
    private readonly IHrReportsService _reports;

    public HrReportExecutionsController(IHrReportsService reports, ILogger<HrReportExecutionsController> logger)
        : base(logger)
    {
        _reports = reports;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ReportExecutionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> List([FromQuery] Guid? reportId, [FromQuery] int take = 25, CancellationToken ct = default) =>
        RunAsync(async a => Ok(await _reports.ListExecutionsAsync(a, reportId, take, ct)), nameof(List));

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ReportExecutionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var execution = await _reports.GetExecutionAsync(a, id, ct);
            return execution == null ? NotFoundResult("Execution") : Ok(execution);
        }, nameof(Get));

    /// <summary>Download info (document id, authenticated path and a short-lived signed URL) for a stored export.</summary>
    [HttpGet("{id:guid}/download")]
    [ProducesResponseType(typeof(ReportExecutionDownloadDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public Task<IActionResult> Download(Guid id, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var download = await _reports.GetExecutionDownloadAsync(a, id, ct);
            return download == null ? NotFoundResult("Execution") : Ok(download);
        }, nameof(Download));
}
