using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs;
using nexthire_api.DTOs.CoreHr;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

/// <summary>
/// Core HR Documents hub: org-wide library over employee documents plus the document request queue.
/// Files are downloaded through <c>GET /api/documents/{documentId}/download-url</c>.
/// </summary>
[ApiController]
[Route("api/hr/documents")]
[Authorize]
public class HrDocumentsController : CoreHrControllerBase
{
    private const long MaxUploadRequestBytes = HrDocumentValidation.MaxUploadBytes + 2 * 1024 * 1024;

    private readonly IHrDocumentService _documents;

    public HrDocumentsController(IHrDocumentService documents, ILogger<HrDocumentsController> logger)
        : base(logger)
    {
        _documents = documents;
    }

    /// <summary>Current user's Core HR capabilities (derived from role codes).</summary>
    [HttpGet("capabilities")]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public Task<IActionResult> GetCapabilities(CancellationToken ct) =>
        RunAsync(async a => Ok(await _documents.GetCapabilitiesAsync(a, ct)), nameof(GetCapabilities));

    /// <summary>Paged, filterable, sortable org-wide document list.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<HrDocumentListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> List([FromQuery] HrDocumentListQuery query, CancellationToken ct) =>
        RunAsync(async a => Ok(await _documents.ListAsync(a, query, ct)), nameof(List));

    /// <summary>KPI counts (total, valid, expiring in 30 days, expired, pending), per-category counts and open requests.</summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(HrDocumentSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Summary(CancellationToken ct) =>
        RunAsync(async a => Ok(await _documents.GetSummaryAsync(a, ct)), nameof(Summary));

    /// <summary>Document counts per canonical category.</summary>
    [HttpGet("categories")]
    [ProducesResponseType(typeof(IReadOnlyList<HrDocumentCategoryCountDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Categories(CancellationToken ct) =>
        RunAsync(async a => Ok(await _documents.GetCategoriesAsync(a, ct)), nameof(Categories));

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(HrDocumentDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var doc = await _documents.GetAsync(a, id, ct);
            return doc == null ? NotFoundResult("Document") : Ok(doc);
        }, nameof(Get));

    /// <summary>Creates a link-only (externalUrl) or pre-uploaded (documentId) document for an employee.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(HrDocumentDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Create([FromBody] CreateHrDocumentRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var created = await _documents.CreateAsync(a, request, ct);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }, nameof(Create));

    /// <summary>Uploads a file (pdf, doc, docx up to 10 MB) and attaches it to an employee.</summary>
    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxUploadRequestBytes)]
    [ProducesResponseType(typeof(HrDocumentDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Upload([FromForm] UploadHrDocumentForm form, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var created = await _documents.UploadAsync(a, form, ct);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }, nameof(Upload));

    /// <summary>Updates document metadata (category, name, expiry, status, externalUrl).</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(HrDocumentDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Update(Guid id, [FromBody] UpsertHrDocumentRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var updated = await _documents.UpdateAsync(a, id, request, ct);
            return updated == null ? NotFoundResult("Document") : Ok(updated);
        }, nameof(Update));

    /// <summary>Archives the document (soft delete via archived_at).</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Archive(Guid id, CancellationToken ct) =>
        RunAsync(async a => await _documents.ArchiveAsync(a, id, ct) ? NoContent() : NotFoundResult("Document"), nameof(Archive));

    // -------------------------------------------------------------------------
    // Document requests
    // -------------------------------------------------------------------------

    /// <summary>Document requests. <c>status</c>: open (default, Open + Overdue), all, or Open / Fulfilled / Cancelled / Overdue.</summary>
    [HttpGet("requests")]
    [ProducesResponseType(typeof(IReadOnlyList<DocumentRequestDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> ListRequests([FromQuery] string? status, [FromQuery] Guid? employeeId, CancellationToken ct) =>
        RunAsync(async a => Ok(await _documents.ListRequestsAsync(a, status, employeeId, ct)), nameof(ListRequests));

    [HttpPost("requests")]
    [ProducesResponseType(typeof(DocumentRequestDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> CreateRequest([FromBody] CreateDocumentRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var created = await _documents.CreateRequestAsync(a, request, ct);
            return StatusCode(StatusCodes.Status201Created, created);
        }, nameof(CreateRequest));

    /// <summary>Marks a request fulfilled, optionally linking the hub document (documentRowId) that satisfies it.</summary>
    [HttpPost("requests/{id:guid}/fulfill")]
    [ProducesResponseType(typeof(DocumentRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> FulfillRequest(Guid id, [FromBody] FulfillDocumentRequestRequest? request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var result = await _documents.FulfillRequestAsync(a, id, request, ct);
            return result == null ? NotFoundResult("Document request") : Ok(result);
        }, nameof(FulfillRequest));

    [HttpPost("requests/{id:guid}/cancel")]
    [ProducesResponseType(typeof(DocumentRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> CancelRequest(Guid id, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var result = await _documents.CancelRequestAsync(a, id, ct);
            return result == null ? NotFoundResult("Document request") : Ok(result);
        }, nameof(CancelRequest));
}
