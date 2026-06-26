using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

[ApiController]
[Route("api/public/orgs")]
[AllowAnonymous]
public class PublicOrgBrandingController : ControllerBase
{
    private readonly IOrganizationSettingsService _settings;
    private readonly ILogger<PublicOrgBrandingController> _logger;

    public PublicOrgBrandingController(
        IOrganizationSettingsService settings,
        ILogger<PublicOrgBrandingController> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Public branding for careers pages and apply flows.
    /// </summary>
    [HttpGet("{orgId:guid}/branding")]
    [ProducesResponseType(typeof(PublicOrganizationBrandingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PublicOrganizationBrandingDto>> GetBranding(
        Guid orgId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (orgId == Guid.Empty)
                return BadRequest(new { message = "orgId is required" });

            var branding = await _settings.GetPublicBrandingAsync(orgId, cancellationToken);
            return Ok(branding);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting public branding for org {OrgId}", orgId);
            return StatusCode(500, new { message = "An error occurred while retrieving organization branding." });
        }
    }
}
