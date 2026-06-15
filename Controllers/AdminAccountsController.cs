using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

/// <summary>
/// Bootstrap admin accounts with email + password using a server-configured secret key.
/// </summary>
[ApiController]
[Route("api/admin")]
[AllowAnonymous]
public class AdminAccountsController : ControllerBase
{
    private readonly IAdminAccountService _adminAccounts;
    private readonly ILogger<AdminAccountsController> _logger;

    public AdminAccountsController(
        IAdminAccountService adminAccounts,
        ILogger<AdminAccountsController> logger)
    {
        _adminAccounts = adminAccounts;
        _logger = logger;
    }

    /// <summary>
    /// Creates an admin user with email/password. Pass orgId to add to an existing org; omit it to create a new org.
    /// </summary>
    [HttpPost("accounts")]
    [ProducesResponseType(typeof(CreateAdminAccountResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<CreateAdminAccountResponseDto>> CreateAccount(
        [FromBody] CreateAdminAccountRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var result = await _adminAccounts.CreateAdminAccountAsync(request, cancellationToken);
            return CreatedAtAction(nameof(CreateAccount), new { id = result.AdminUserId }, result);
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized(new { message = "Invalid secret key." });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Admin account bootstrap configuration error");
            return StatusCode(503, new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating admin account");
            return StatusCode(500, new { message = "An error occurred while creating the admin account." });
        }
    }
}
