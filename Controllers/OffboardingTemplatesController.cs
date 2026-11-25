using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs.CoreHr;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

/// <summary>Offboarding templates (task checklists snapshotted into each new offboarding).</summary>
[ApiController]
[Route("api/offboarding/templates")]
[Authorize]
public class OffboardingTemplatesController : CoreHrControllerBase
{
    private readonly IOffboardingService _offboardings;

    public OffboardingTemplatesController(IOffboardingService offboardings, ILogger<OffboardingTemplatesController> logger)
        : base(logger)
    {
        _offboardings = offboardings;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<OffboardingTemplateListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> List([FromQuery] OffboardingTemplateListQuery query, CancellationToken ct) =>
        RunAsync(async a => Ok(await _offboardings.ListTemplatesAsync(a, query, ct)), nameof(List));

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(OffboardingTemplateDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var template = await _offboardings.GetTemplateAsync(a, id, ct);
            return template == null ? NotFoundResult("Template") : Ok(template);
        }, nameof(Get));

    [HttpPost]
    [ProducesResponseType(typeof(OffboardingTemplateDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Create([FromBody] UpsertOffboardingTemplateRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var created = await _offboardings.CreateTemplateAsync(a, request, ct);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }, nameof(Create));

    /// <summary>Updates the template and replaces its tasks (version is bumped; existing offboardings keep their snapshot).</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(OffboardingTemplateDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Update(Guid id, [FromBody] UpsertOffboardingTemplateRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var updated = await _offboardings.UpdateTemplateAsync(a, id, request, ct);
            return updated == null ? NotFoundResult("Template") : Ok(updated);
        }, nameof(Update));

    [HttpPost("{id:guid}/duplicate")]
    [ProducesResponseType(typeof(OffboardingTemplateDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Duplicate(Guid id, [FromBody] DuplicateOffboardingTemplateRequest? request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var copy = await _offboardings.DuplicateTemplateAsync(a, id, request, ct);
            return copy == null ? NotFoundResult("Template") : CreatedAtAction(nameof(Get), new { id = copy.Id }, copy);
        }, nameof(Duplicate));

    /// <summary>Makes the template the organization default (must be active and not archived).</summary>
    [HttpPost("{id:guid}/set-default")]
    [ProducesResponseType(typeof(OffboardingTemplateDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> SetDefault(Guid id, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var template = await _offboardings.SetDefaultTemplateAsync(a, id, ct);
            return template == null ? NotFoundResult("Template") : Ok(template);
        }, nameof(SetDefault));

    /// <summary>Archives the template (soft delete). Existing offboardings are unaffected.</summary>
    [HttpPost("{id:guid}/archive")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Archive(Guid id, CancellationToken ct) =>
        RunAsync(async a => await _offboardings.ArchiveTemplateAsync(a, id, ct) ? NoContent() : NotFoundResult("Template"), nameof(Archive));

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
        RunAsync(async a => await _offboardings.ArchiveTemplateAsync(a, id, ct) ? NoContent() : NotFoundResult("Template"), nameof(Delete));
}
