using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

/// <summary>
/// Trial / public registration: provisions org + admin in Nexa (no prior login).
/// </summary>
[ApiController]
[Route("api/onboarding")]
[AllowAnonymous]
public class OnboardingProvisionController : ControllerBase
{
    private readonly IOrgProvisioningService _provisioning;
    private readonly ILogger<OnboardingProvisionController> _logger;

    public OnboardingProvisionController(
        IOrgProvisioningService provisioning,
        ILogger<OnboardingProvisionController> logger)
    {
        _provisioning = provisioning;
        _logger = logger;
    }

    /// <summary>
    /// Creates organization and admin user in Nexa via server-side provisioning key.
    /// </summary>
    [HttpPost("provision")]
    [ProducesResponseType(typeof(ProvisionOrganizationResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ProvisionOrganizationResponseDto>> Provision(
        [FromBody] ProvisionOrganizationRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var result = await _provisioning.ProvisionAsync(request, cancellationToken);
            return CreatedAtAction(nameof(Provision), new { id = result.OrganizationId }, result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Provisioning configuration error");
            return StatusCode(503, new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error provisioning organization");
            return StatusCode(500, new { message = "An error occurred while creating your organization." });
        }
    }
}
