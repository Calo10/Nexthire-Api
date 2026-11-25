using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs.CoreHr;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

/// <summary>
/// Core HR Reports &amp; People Analytics: analytics sections, dataset catalog, saved report definitions and
/// preview / execute / export. Dashboards, schedules and executions live in their own controllers.
/// Capabilities derive from role codes (<c>ReportsPermissions</c>). Recruitment data is never exposed here.
/// </summary>
[ApiController]
[Route("api/hr/reports")]
[Authorize]
public class HrReportsController : CoreHrControllerBase
{
    private readonly IHrReportsService _reports;

    public HrReportsController(IHrReportsService reports, ILogger<HrReportsController> logger)
        : base(logger)
    {
        _reports = reports;
    }

    // -------------------------------------------------------------------------
    // Capabilities / analytics
    // -------------------------------------------------------------------------

    /// <summary>Current user's Reports capabilities (derived from role codes).</summary>
    [HttpGet("capabilities")]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public Task<IActionResult> GetCapabilities(CancellationToken ct) =>
        RunAsync(async a => Ok(await _reports.GetCapabilitiesAsync(a, ct)), nameof(GetCapabilities));

    /// <summary>Overview KPIs, headcount evolution, workforce split, onboarding/offboarding/documents performance and insights.</summary>
    [HttpGet("analytics/overview")]
    [ProducesResponseType(typeof(HrReportsOverviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Overview([FromQuery] HrReportsFilterQuery filter, CancellationToken ct) =>
        RunAsync(async a => Ok(await _reports.GetOverviewAsync(a, filter, ct)), nameof(Overview));

    [HttpGet("analytics/workforce")]
    [ProducesResponseType(typeof(HrWorkforceAnalyticsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Workforce([FromQuery] HrReportsFilterQuery filter, CancellationToken ct) =>
        RunAsync(async a => Ok(await _reports.GetWorkforceAsync(a, filter, ct)), nameof(Workforce));

    /// <summary>Retention analytics. Separation reasons are masked unless the caller can view confidential offboarding data.</summary>
    [HttpGet("analytics/retention")]
    [ProducesResponseType(typeof(HrRetentionAnalyticsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Retention([FromQuery] HrReportsFilterQuery filter, CancellationToken ct) =>
        RunAsync(async a => Ok(await _reports.GetRetentionAsync(a, filter, ct)), nameof(Retention));

    [HttpGet("analytics/onboarding")]
    [ProducesResponseType(typeof(HrOnboardingAnalyticsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Onboarding([FromQuery] HrReportsFilterQuery filter, CancellationToken ct) =>
        RunAsync(async a => Ok(await _reports.GetOnboardingAsync(a, filter, ct)), nameof(Onboarding));

    [HttpGet("analytics/offboarding")]
    [ProducesResponseType(typeof(HrOffboardingAnalyticsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Offboarding([FromQuery] HrReportsFilterQuery filter, CancellationToken ct) =>
        RunAsync(async a => Ok(await _reports.GetOffboardingAsync(a, filter, ct)), nameof(Offboarding));

    [HttpGet("analytics/organization")]
    [ProducesResponseType(typeof(HrOrganizationAnalyticsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Organization([FromQuery] HrReportsFilterQuery filter, CancellationToken ct) =>
        RunAsync(async a => Ok(await _reports.GetOrganizationAsync(a, filter, ct)), nameof(Organization));

    [HttpGet("analytics/documents")]
    [ProducesResponseType(typeof(HrDocumentsAnalyticsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Documents([FromQuery] HrReportsFilterQuery filter, CancellationToken ct) =>
        RunAsync(async a => Ok(await _reports.GetDocumentsAsync(a, filter, ct)), nameof(Documents));

    /// <summary>Annualized compensation aggregates (single currency, minimum cohort of 5). Requires Reports.ViewCompensation.</summary>
    [HttpGet("analytics/compensation")]
    [ProducesResponseType(typeof(HrCompensationAnalyticsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Compensation([FromQuery] HrReportsFilterQuery filter, CancellationToken ct) =>
        RunAsync(async a => Ok(await _reports.GetCompensationAsync(a, filter, ct)), nameof(Compensation));

    // -------------------------------------------------------------------------
    // Datasets
    // -------------------------------------------------------------------------

    /// <summary>Datasets the caller may use (with the fields they may access).</summary>
    [HttpGet("datasets")]
    [ProducesResponseType(typeof(IReadOnlyList<ReportDatasetDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> ListDatasets(CancellationToken ct) =>
        RunAsync(async a => Ok(await _reports.ListDatasetsAsync(a, ct)), nameof(ListDatasets));

    [HttpGet("datasets/{key}")]
    [ProducesResponseType(typeof(ReportDatasetDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> GetDataset(string key, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var dataset = await _reports.GetDatasetAsync(a, key, ct);
            return dataset == null ? NotFoundResult("Dataset") : Ok(dataset);
        }, nameof(GetDataset));

    [HttpGet("datasets/{key}/fields")]
    [ProducesResponseType(typeof(IReadOnlyList<ReportFieldDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> GetDatasetFields(string key, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var fields = await _reports.GetDatasetFieldsAsync(a, key, ct);
            return fields == null ? NotFoundResult("Dataset") : Ok(fields);
        }, nameof(GetDatasetFields));

    // -------------------------------------------------------------------------
    // Report definitions
    // -------------------------------------------------------------------------

    /// <summary>Reports the caller can see (system, own, shared). Seeds the system report cards on first use.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ReportDefinitionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> List(
        [FromQuery] string? category, [FromQuery] string? search, [FromQuery] bool favoritesOnly = false, CancellationToken ct = default) =>
        RunAsync(async a => Ok(await _reports.ListReportsAsync(a, category, search, favoritesOnly, ct)), nameof(List));

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ReportDefinitionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var report = await _reports.GetReportAsync(a, id, ct);
            return report == null ? NotFoundResult("Report") : Ok(report);
        }, nameof(Get));

    [HttpPost]
    [ProducesResponseType(typeof(ReportDefinitionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Create([FromBody] CreateReportDefinitionRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var created = await _reports.CreateReportAsync(a, request, ct);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }, nameof(Create));

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(ReportDefinitionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Update(Guid id, [FromBody] UpdateReportDefinitionRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var updated = await _reports.UpdateReportAsync(a, id, request, ct);
            return updated == null ? NotFoundResult("Report") : Ok(updated);
        }, nameof(Update));

    /// <summary>Creates a private copy owned by the caller.</summary>
    [HttpPost("{id:guid}/duplicate")]
    [ProducesResponseType(typeof(ReportDefinitionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Duplicate(Guid id, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var copy = await _reports.DuplicateReportAsync(a, id, ct);
            return copy == null ? NotFoundResult("Report") : CreatedAtAction(nameof(Get), new { id = copy.Id }, copy);
        }, nameof(Duplicate));

    /// <summary>Soft-archives a custom report (system reports cannot be archived).</summary>
    [HttpPost("{id:guid}/archive")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Archive(Guid id, CancellationToken ct) =>
        RunAsync(async a => await _reports.ArchiveReportAsync(a, id, ct) ? NoContent() : NotFoundResult("Report"), nameof(Archive));

    [HttpPost("{id:guid}/favorite")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> AddFavorite(Guid id, CancellationToken ct) =>
        RunAsync(async a => await _reports.SetFavoriteAsync(a, id, true, ct) ? NoContent() : NotFoundResult("Report"), nameof(AddFavorite));

    [HttpDelete("{id:guid}/favorite")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> RemoveFavorite(Guid id, CancellationToken ct) =>
        RunAsync(async a => await _reports.SetFavoriteAsync(a, id, false, ct) ? NoContent() : NotFoundResult("Report"), nameof(RemoveFavorite));

    // -------------------------------------------------------------------------
    // Run
    // -------------------------------------------------------------------------

    /// <summary>Runs a saved report. Optional body overrides From / To / DepartmentId / Limit.</summary>
    [HttpPost("{id:guid}/execute")]
    [ProducesResponseType(typeof(ReportExecutionResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Execute(
        Guid id, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] ReportPreviewRequest? request,
        CancellationToken ct) =>
        RunAsync(async a =>
        {
            var result = await _reports.ExecuteAsync(a, id, request, ct);
            return result == null ? NotFoundResult("Report") : Ok(result);
        }, nameof(Execute));

    /// <summary>Runs an unsaved configuration (not recorded in execution history).</summary>
    [HttpPost("preview")]
    [ProducesResponseType(typeof(ReportExecutionResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Preview([FromBody] ReportPreviewRequest request, CancellationToken ct) =>
        RunAsync(async a => Ok(await _reports.PreviewAsync(a, request, ct)), nameof(Preview));

    /// <summary>
    /// Exports a saved report as CSV. Returns the file for immediate download by default (headers
    /// <c>X-Report-Execution-Id</c> / <c>X-Report-Document-Id</c> identify the persisted execution and stored copy);
    /// pass <c>?response=json</c> to get <see cref="ReportExportResultDto"/> (including <c>downloadPath</c>) instead.
    /// </summary>
    [HttpPost("{id:guid}/export")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ReportExportResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Export(
        Guid id,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] ReportExportRequest? request,
        [FromQuery] string? response,
        CancellationToken ct) =>
        RunAsync(async a =>
        {
            var outcome = await _reports.ExportAsync(a, id, request ?? new ReportExportRequest(), ct);
            if (outcome == null) return NotFoundResult("Report");

            if (string.Equals(response, "json", StringComparison.OrdinalIgnoreCase))
                return Ok(outcome.Result);

            Response.Headers["X-Report-Execution-Id"] = outcome.Result.ExecutionId.ToString();
            if (outcome.Result.DocumentId.HasValue)
                Response.Headers["X-Report-Document-Id"] = outcome.Result.DocumentId.Value.ToString();
            Response.Headers["Access-Control-Expose-Headers"] = "X-Report-Execution-Id, X-Report-Document-Id, Content-Disposition";
            return File(outcome.Content, outcome.ContentType, outcome.FileName);
        }, nameof(Export));
}
