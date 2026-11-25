using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs;
using nexthire_api.DTOs.CoreHr;
using nexthire_api.Exceptions;
using nexthire_api.Security;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

[ApiController]
[Route("api/employees")]
[Authorize]
public class EmployeesController : ControllerBase
{
    private const long MaxImportBytes = 5 * 1024 * 1024;

    private readonly IEmployeeService _employees;
    private readonly ILogger<EmployeesController> _logger;

    public EmployeesController(IEmployeeService employees, ILogger<EmployeesController> logger)
    {
        _employees = employees;
        _logger = logger;
    }

    private HrActor Actor() => new(
        ClaimUtils.RequireOrgId(User),
        ClaimUtils.RequireNexaUserId(User),
        ClaimUtils.GetDisplayName(User) ?? ClaimUtils.GetEmail(User));

    /// <summary>Runs an action and maps service/auth exceptions to HTTP responses.</summary>
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
        catch (OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during employees operation: {Operation}", operation);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while processing the employees request." });
        }
    }

    private static NotFoundObjectResult NotFoundEmployee() => new(new { message = "Employee not found." });

    // -------------------------------------------------------------------------
    // Employees
    // -------------------------------------------------------------------------

    /// <summary>Current user's Core HR capabilities (derived from role codes).</summary>
    [HttpGet("capabilities")]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public Task<IActionResult> GetCapabilities(CancellationToken ct) =>
        RunAsync(async a => Ok(await _employees.GetCapabilitiesAsync(a, ct)), nameof(GetCapabilities));

    /// <summary>Paged, filterable, sortable employee list.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<EmployeeListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> List([FromQuery] EmployeeListQuery query, CancellationToken ct) =>
        RunAsync(async a => Ok(await _employees.ListAsync(a, query, ct)), nameof(List));

    /// <summary>Headcount summary and period comparison.</summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(EmployeeSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Summary([FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct) =>
        RunAsync(async a => Ok(await _employees.GetSummaryAsync(a, from, to, ct)), nameof(Summary));

    /// <summary>Employee detail (sensitive fields are null when the role lacks Employees.ViewSensitiveData).</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(EmployeeDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var detail = await _employees.GetAsync(a, id, ct);
            return detail == null ? NotFoundEmployee() : Ok(detail);
        }, nameof(Get));

    [HttpPost]
    [ProducesResponseType(typeof(EmployeeDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Create([FromBody] CreateEmployeeRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var created = await _employees.CreateAsync(a, request, ct);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }, nameof(Create));

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(EmployeeDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Update(Guid id, [FromBody] UpdateEmployeeRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var updated = await _employees.UpdateAsync(a, id, request, ct);
            return updated == null ? NotFoundEmployee() : Ok(updated);
        }, nameof(Update));

    /// <summary>Archives the employee (soft delete via archived_at).</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
        RunAsync(async a => await _employees.DeleteAsync(a, id, ct) ? NoContent() : NotFoundEmployee(), nameof(Delete));

    [HttpPost("{id:guid}/restore")]
    [ProducesResponseType(typeof(EmployeeDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Restore(Guid id, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var restored = await _employees.RestoreAsync(a, id, ct);
            return restored == null ? NotFoundEmployee() : Ok(restored);
        }, nameof(Restore));

    [HttpPost("{id:guid}/status")]
    [ProducesResponseType(typeof(EmployeeDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> ChangeStatus(Guid id, [FromBody] ChangeEmployeeStatusRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var updated = await _employees.ChangeStatusAsync(a, id, request, ct);
            return updated == null ? NotFoundEmployee() : Ok(updated);
        }, nameof(ChangeStatus));

    [HttpGet("{id:guid}/activity")]
    [ProducesResponseType(typeof(IReadOnlyList<EmployeeActivityDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Activity(Guid id, [FromQuery] int take = 50, CancellationToken ct = default) =>
        RunAsync(async a => Ok(await _employees.GetActivityAsync(a, id, take, ct)), nameof(Activity));

    [HttpGet("{id:guid}/history")]
    [ProducesResponseType(typeof(IReadOnlyList<EmploymentHistoryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> History(Guid id, CancellationToken ct) =>
        RunAsync(async a => Ok(await _employees.GetHistoryAsync(a, id, ct)), nameof(History));

    // -------------------------------------------------------------------------
    // Documents
    // -------------------------------------------------------------------------

    [HttpGet("{id:guid}/documents")]
    [ProducesResponseType(typeof(IReadOnlyList<EmployeeDocumentDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> ListDocuments(Guid id, [FromQuery] bool includeArchived = false, CancellationToken ct = default) =>
        RunAsync(async a => Ok(await _employees.ListDocumentsAsync(a, id, includeArchived, ct)), nameof(ListDocuments));

    [HttpPost("{id:guid}/documents")]
    [ProducesResponseType(typeof(EmployeeDocumentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> AddDocument(Guid id, [FromBody] UpsertEmployeeDocumentRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var doc = await _employees.AddDocumentAsync(a, id, request, ct);
            return Created($"/api/employees/{id}/documents/{doc.Id}", doc);
        }, nameof(AddDocument));

    /// <summary>Archives an employee document (soft delete).</summary>
    [HttpDelete("{id:guid}/documents/{documentId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> DeleteDocument(Guid id, Guid documentId, CancellationToken ct) =>
        RunAsync(async a => await _employees.ArchiveDocumentAsync(a, id, documentId, ct)
            ? NoContent()
            : new NotFoundObjectResult(new { message = "Document not found." }), nameof(DeleteDocument));

    // -------------------------------------------------------------------------
    // Compensation
    // -------------------------------------------------------------------------

    [HttpGet("{id:guid}/compensation")]
    [ProducesResponseType(typeof(IReadOnlyList<CompensationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> ListCompensation(Guid id, CancellationToken ct) =>
        RunAsync(async a => Ok(await _employees.ListCompensationAsync(a, id, ct)), nameof(ListCompensation));

    [HttpPost("{id:guid}/compensation")]
    [ProducesResponseType(typeof(CompensationDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> AddCompensation(Guid id, [FromBody] UpsertCompensationRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var comp = await _employees.AddCompensationAsync(a, id, request, ct);
            return Created($"/api/employees/{id}/compensation", comp);
        }, nameof(AddCompensation));

    // -------------------------------------------------------------------------
    // Convert / import / export
    // -------------------------------------------------------------------------

    /// <summary>Creates (or returns the existing) employee for a hired candidate.</summary>
    [HttpPost("convert-from-candidate")]
    [ProducesResponseType(typeof(EmployeeDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> ConvertFromCandidate([FromBody] ConvertCandidateToEmployeeRequest request, CancellationToken ct) =>
        RunAsync(async a => Ok(await _employees.ConvertFromCandidateAsync(a, request, ct)), nameof(ConvertFromCandidate));

    /// <summary>
    /// Imports employees from CSV. Send either multipart/form-data with a file part, or the raw CSV text as the request body.
    /// Columns: FirstName, LastName, WorkEmail, EmployeeNumber, Department, JobTitle, Location, EmploymentStatus, EmploymentType, HireDate.
    /// </summary>
    [HttpPost("import")]
    [RequestSizeLimit(MaxImportBytes)]
    [ProducesResponseType(typeof(EmployeeImportResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Import(CancellationToken ct) =>
        RunAsync(async a =>
        {
            string csv;
            if (Request.HasFormContentType)
            {
                var form = await Request.ReadFormAsync(ct);
                var file = form.Files.FirstOrDefault();
                if (file == null || file.Length == 0)
                    return BadRequest(new { message = "Attach a CSV file." });
                if (file.Length > MaxImportBytes)
                    return StatusCode(StatusCodes.Status413PayloadTooLarge, new { message = "CSV file is too large (5 MB max)." });
                using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                csv = await reader.ReadToEndAsync(ct);
            }
            else
            {
                using var reader = new StreamReader(Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                csv = await reader.ReadToEndAsync(ct);
            }

            if (string.IsNullOrWhiteSpace(csv))
                return BadRequest(new { message = "The CSV is empty." });

            return Ok(await _employees.ImportAsync(a, csv, ct));
        }, nameof(Import));

    /// <summary>Exports the current filter results as CSV (max 5000 rows).</summary>
    [HttpGet("export")]
    [Produces("text/csv")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK, "text/csv")]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Export([FromQuery] EmployeeListQuery query, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var csv = await _employees.ExportAsync(a, query, ct);
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()
                .Concat(Encoding.UTF8.GetBytes(csv))
                .ToArray();
            return File(bytes, "text/csv", $"employees-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
        }, nameof(Export));
}
