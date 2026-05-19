using System.Data.SqlClient;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs;
using nexthire_api.Security;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public class RolesController : ControllerBase
{
    private readonly IRoleService _roleService;
    private readonly ILogger<RolesController> _logger;

    public RolesController(IRoleService roleService, ILogger<RolesController> logger)
    {
        _roleService = roleService;
        _logger = logger;
    }

    [HttpGet("roles")]
    [ProducesResponseType(typeof(IEnumerable<RoleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IEnumerable<RoleDto>>> GetRoles()
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var roles = await _roleService.GetRolesAsync(orgId);
            return Ok(roles);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing roles");
            return StatusCode(500, new { message = "An error occurred while retrieving roles" });
        }
    }

    [HttpGet("users/{userId:guid}/roles")]
    [ProducesResponseType(typeof(IEnumerable<UserRoleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<UserRoleDto>>> GetUserRoles(Guid userId)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);

            var (roles, userNotFound) = await _roleService.GetUserRolesAsync(orgId, userId);
            if (userNotFound)
                return NotFound(new { message = $"User with ID {userId} not found" });

            return Ok(roles);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing roles for user {UserId}", userId);
            return StatusCode(500, new { message = "An error occurred while retrieving user roles" });
        }
    }

    [HttpPost("users/{userId:guid}/roles")]
    [ProducesResponseType(typeof(UserRoleDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserRoleDto>> AssignUserRole(Guid userId, [FromBody] AssignUserRoleRequestDto dto)
    {
        try
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var orgId = ClaimUtils.RequireOrgId(User);

            var (assignment, userNotFound, roleNotFound, duplicate) =
                await _roleService.AssignRoleAsync(orgId, userId, dto);

            if (userNotFound)
                return NotFound(new { message = $"User with ID {userId} not found" });
            if (roleNotFound)
                return NotFound(new { message = $"Role with ID {dto.RoleId} not found" });
            if (duplicate)
                return Conflict(new { message = "This role is already assigned to the user." });

            return CreatedAtAction(nameof(GetUserRoles), new { userId }, assignment);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627)
        {
            return Conflict(new { message = "This role is already assigned to the user." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error assigning role to user {UserId}", userId);
            return StatusCode(500, new { message = "An error occurred while assigning the role" });
        }
    }

    [HttpDelete("users/{userId:guid}/roles/{roleId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveUserRole(Guid userId, Guid roleId)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);

            var (removed, notFound, userNotFound, roleNotFound) =
                await _roleService.RemoveRoleAsync(orgId, userId, roleId);

            if (userNotFound)
                return NotFound(new { message = $"User with ID {userId} not found" });
            if (roleNotFound)
                return NotFound(new { message = $"Role with ID {roleId} not found" });
            if (notFound)
                return NotFound(new { message = "Role assignment was not found." });

            if (!removed)
                return StatusCode(500, new { message = "Unable to remove role assignment" });

            return NoContent();
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing role {RoleId} from user {UserId}", roleId, userId);
            return StatusCode(500, new { message = "An error occurred while removing the role" });
        }
    }
}
