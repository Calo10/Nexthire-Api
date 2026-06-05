using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs;
using nexthire_api.Security;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

/// <summary>
/// Org-scoped platform users (Nexa membership + nh_users + optional NextHire roles).
/// </summary>
[ApiController]
[Route("api/org/users")]
[Authorize]
public class OrgUsersController : ControllerBase
{
    private readonly IOrgUserService _orgUsers;
    private readonly ILogger<OrgUsersController> _logger;

    public OrgUsersController(IOrgUserService orgUsers, ILogger<OrgUsersController> logger)
    {
        _orgUsers = orgUsers;
        _logger = logger;
    }

    /// <summary>
    /// List users for the current organization (syncs Nexa members + pending invites).
    /// Send header <c>X-Nexa-Access-Token</c> (from login/consume response) when the server was restarted.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<OrgUserDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<OrgUserDto>>> List(CancellationToken cancellationToken)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var nexaUserId = ClaimUtils.RequireNexaUserId(User).ToString();
            var users = await _orgUsers.ListAsync(orgId, nexaUserId, cancellationToken);
            return Ok(users);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing org users");
            return StatusCode(500, new { message = "An error occurred while retrieving users." });
        }
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(OrgUserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrgUserDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var user = await _orgUsers.GetAsync(orgId, id, cancellationToken);
            if (user is null)
                return NotFound(new { message = "User not found in this organization." });
            return Ok(user);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Invite a new user to the org via Nexa and create/update nh_users (+ optional NextHire role).
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(CreateOrgUserResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<CreateOrgUserResponseDto>> Invite(
        [FromBody] CreateOrgUserRequestDto dto,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var orgId = ClaimUtils.RequireOrgId(User);
            var nexaUserId = ClaimUtils.RequireNexaUserId(User).ToString();
            var result = await _orgUsers.InviteAsync(orgId, nexaUserId, dto, cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = result.User.Id }, result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inviting org user");
            return StatusCode(500, new { message = "An error occurred while inviting the user." });
        }
    }

    [HttpPatch("{id:guid}")]
    [ProducesResponseType(typeof(OrgUserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrgUserDto>> Update(
        Guid id,
        [FromBody] UpdateOrgUserRequestDto dto,
        CancellationToken cancellationToken)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var user = await _orgUsers.UpdateAsync(orgId, id, dto, cancellationToken);
            if (user is null)
                return NotFound(new { message = "User not found in this organization." });
            return Ok(user);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
    }
}
