using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs;
using nexthire_api.Repositories;
using nexthire_api.Security;

namespace nexthire_api.Controllers;

[ApiController]
[Route("api/templates")]
[Authorize]
public class TemplatesController : ControllerBase
{
    private readonly ITemplatesRepository _repo;
    private readonly ILogger<TemplatesController> _logger;

    public TemplatesController(ITemplatesRepository repo, ILogger<TemplatesController> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    /// <summary>
    /// List templates for the current org.
    /// </summary>
    /// <remarks>
    /// Optional filter:
    /// - channel: email | whatsapp
    ///
    /// Curl:
    /// curl -H "Authorization: Bearer $TOKEN" "http://localhost:5000/api/templates?channel=email"
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<TemplateListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IEnumerable<TemplateListItemDto>>> List([FromQuery] string? channel)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var channelNormalized = NormalizeChannelOrNull(channel);
            if (channel != null && channelNormalized == null)
                return BadRequest(new { message = "Invalid channel. Allowed: email, whatsapp" });

            var items = await _repo.ListAsync(orgId, channelNormalized);
            return Ok(items);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing templates");
            return StatusCode(500, new { message = "An error occurred while retrieving templates" });
        }
    }

    /// <summary>
    /// Get template by id (org-scoped).
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(TemplateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TemplateDto>> Get(Guid id)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var template = await _repo.GetAsync(orgId, id);
            if (template == null)
                return NotFound(new { message = $"Template with ID {id} not found" });

            return Ok(template);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting template {TemplateId}", id);
            return StatusCode(500, new { message = "An error occurred while retrieving the template" });
        }
    }

    /// <summary>
    /// Create a template (org-scoped).
    /// </summary>
    /// <remarks>
    /// Curl:
    /// curl -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    ///   -d '{"name":"Welcome","channel":"email","subject":"Welcome","body":"Hello","isActive":true}' \
    ///   "http://localhost:5000/api/templates"
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(typeof(TemplateDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<TemplateDto>> Create([FromBody] CreateTemplateRequestDto request)
    {
        try
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var orgId = ClaimUtils.RequireOrgId(User);

            var channelNormalized = NormalizeChannelOrNull(request.Channel);
            if (channelNormalized == null)
                return BadRequest(new { message = "Invalid channel. Allowed: email, whatsapp" });

            var subjectNormalized = NormalizeSubject(channelNormalized, request.Subject);
            if (channelNormalized == "email" && string.IsNullOrWhiteSpace(subjectNormalized))
                return BadRequest(new { message = "subject is required when channel=email" });

            if (string.IsNullOrWhiteSpace(request.Body))
                return BadRequest(new { message = "body is required" });

            var isActive = request.IsActive ?? true;

            var created = await _repo.CreateAsync(orgId, request, channelNormalized, subjectNormalized, isActive);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating template");
            return StatusCode(500, new { message = "An error occurred while creating the template" });
        }
    }

    /// <summary>
    /// Update a template (org-scoped).
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(TemplateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TemplateDto>> Update(Guid id, [FromBody] UpdateTemplateRequestDto request)
    {
        try
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var orgId = ClaimUtils.RequireOrgId(User);

            var existing = await _repo.GetAsync(orgId, id);
            if (existing == null)
                return NotFound(new { message = $"Template with ID {id} not found" });

            var channelNormalized = NormalizeChannelOrNull(existing.Channel) ?? existing.Channel;
            var subjectNormalized = NormalizeSubject(channelNormalized, request.Subject);
            if (channelNormalized == "email" && string.IsNullOrWhiteSpace(subjectNormalized))
                return BadRequest(new { message = "subject is required when channel=email" });

            if (string.IsNullOrWhiteSpace(request.Body))
                return BadRequest(new { message = "body is required" });

            var isActive = request.IsActive ?? existing.IsActive;

            var updated = await _repo.UpdateAsync(orgId, id, request, channelNormalized, subjectNormalized, isActive);
            if (updated == null)
                return NotFound(new { message = $"Template with ID {id} not found" });

            return Ok(updated);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating template {TemplateId}", id);
            return StatusCode(500, new { message = "An error occurred while updating the template" });
        }
    }

    /// <summary>
    /// Hard delete template (DELETE FROM message_templates).
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);

            var deleted = await _repo.DeleteAsync(orgId, id);
            if (!deleted)
                return NotFound(new { message = $"Template with ID {id} not found" });

            return NoContent();
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting template {TemplateId}", id);
            return StatusCode(500, new { message = "An error occurred while deleting the template" });
        }
    }

    private static string? NormalizeChannelOrNull(string? channel)
    {
        if (channel == null) return null;
        var c = channel.Trim().ToLowerInvariant();
        return c is "email" or "whatsapp" ? c : null;
    }

    private static string? NormalizeSubject(string channel, string? subject)
    {
        if (channel == "whatsapp")
            return string.IsNullOrWhiteSpace(subject) ? null : subject.Trim();

        // email
        return string.IsNullOrWhiteSpace(subject) ? null : subject.Trim();
    }
}

