using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs.CoreHr;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

/// <summary>Onboarding templates. Route is distinct from <c>api/onboarding/provision</c> (organization provisioning).</summary>
[ApiController]
[Route("api/onboarding/templates")]
[Authorize]
public class OnboardingTemplatesController : CoreHrControllerBase
{
    private readonly IOnboardingService _onboardings;

    public OnboardingTemplatesController(IOnboardingService onboardings, ILogger<OnboardingTemplatesController> logger)
        : base(logger)
    {
        _onboardings = onboardings;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<OnboardingTemplateListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> List([FromQuery] OnboardingTemplateListQuery query, CancellationToken ct) =>
        RunAsync(async a => Ok(await _onboardings.ListTemplatesAsync(a, query, ct)), nameof(List));

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(OnboardingTemplateDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var template = await _onboardings.GetTemplateAsync(a, id, ct);
            return template == null ? NotFoundResult("Template") : Ok(template);
        }, nameof(Get));

    [HttpPost]
    [ProducesResponseType(typeof(OnboardingTemplateDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Create([FromBody] UpsertOnboardingTemplateRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var created = await _onboardings.CreateTemplateAsync(a, request, ct);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }, nameof(Create));

    /// <summary>Updates the template and replaces its tasks (version is bumped; existing onboardings keep their snapshot).</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(OnboardingTemplateDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Update(Guid id, [FromBody] UpsertOnboardingTemplateRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var updated = await _onboardings.UpdateTemplateAsync(a, id, request, ct);
            return updated == null ? NotFoundResult("Template") : Ok(updated);
        }, nameof(Update));

    [HttpPost("{id:guid}/duplicate")]
    [ProducesResponseType(typeof(OnboardingTemplateDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Duplicate(Guid id, [FromBody] DuplicateOnboardingTemplateRequest? request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var copy = await _onboardings.DuplicateTemplateAsync(a, id, request, ct);
            return copy == null ? NotFoundResult("Template") : CreatedAtAction(nameof(Get), new { id = copy.Id }, copy);
        }, nameof(Duplicate));

    /// <summary>Archives the template (soft delete). Existing onboardings are unaffected.</summary>
    [HttpPost("{id:guid}/archive")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Archive(Guid id, CancellationToken ct) =>
        RunAsync(async a => await _onboardings.ArchiveTemplateAsync(a, id, ct) ? NoContent() : NotFoundResult("Template"), nameof(Archive));

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
        RunAsync(async a => await _onboardings.ArchiveTemplateAsync(a, id, ct) ? NoContent() : NotFoundResult("Template"), nameof(Delete));
}
