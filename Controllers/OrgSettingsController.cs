using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs;
using nexthire_api.Security;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

[ApiController]
[Route("api/org/settings")]
[Authorize]
public class OrgSettingsController : ControllerBase
{
    private readonly IOrganizationSettingsService _settings;
    private readonly IOrgMembershipAuthorizationService _orgAuth;
    private readonly ILogger<OrgSettingsController> _logger;

    public OrgSettingsController(
        IOrganizationSettingsService settings,
        IOrgMembershipAuthorizationService orgAuth,
        ILogger<OrgSettingsController> logger)
    {
        _settings = settings;
        _orgAuth = orgAuth;
        _logger = logger;
    }

    /// <summary>
    /// Get organization branding settings for the current org (owner/admin only).
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(OrganizationSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<OrganizationSettingsDto>> Get(CancellationToken cancellationToken)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var nexaUserId = ClaimUtils.RequireNexaUserId(User);
            await _orgAuth.EnsureOwnerOrAdminAsync(orgId, nexaUserId, cancellationToken);

            var settings = await _settings.GetForOrgAsync(orgId, cancellationToken);
            return Ok(settings);
        }
        catch (UnauthorizedAccessException ex)
        {
            var status = ex.Message.Contains("owners and admins", StringComparison.OrdinalIgnoreCase)
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status401Unauthorized;
            return StatusCode(status, new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting organization settings");
            return StatusCode(500, new { message = "An error occurred while retrieving organization settings." });
        }
    }

    /// <summary>
    /// List available color palette presets.
    /// </summary>
    [HttpGet("palettes")]
    [ProducesResponseType(typeof(IReadOnlyList<OrganizationColorPaletteOptionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<OrganizationColorPaletteOptionDto>>> ListPalettes(
        CancellationToken cancellationToken)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var nexaUserId = ClaimUtils.RequireNexaUserId(User);
            await _orgAuth.EnsureOwnerOrAdminAsync(orgId, nexaUserId, cancellationToken);
            return Ok(_settings.ListPaletteOptions());
        }
        catch (UnauthorizedAccessException ex)
        {
            var status = ex.Message.Contains("owners and admins", StringComparison.OrdinalIgnoreCase)
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status401Unauthorized;
            return StatusCode(status, new { message = ex.Message });
        }
    }

    /// <summary>
    /// Create or update organization branding settings (owner/admin only).
    /// </summary>
    [HttpPut]
    [ProducesResponseType(typeof(OrganizationSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<OrganizationSettingsDto>> Upsert(
        [FromBody] UpsertOrganizationSettingsRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var orgId = ClaimUtils.RequireOrgId(User);
            var nexaUserId = ClaimUtils.RequireNexaUserId(User);
            var settings = await _settings.UpsertForOrgAsync(orgId, nexaUserId, request, cancellationToken);
            return Ok(settings);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            var status = ex.Message.Contains("owners and admins", StringComparison.OrdinalIgnoreCase)
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status401Unauthorized;
            return StatusCode(status, new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating organization settings");
            return StatusCode(500, new { message = "An error occurred while updating organization settings." });
        }
    }
}
