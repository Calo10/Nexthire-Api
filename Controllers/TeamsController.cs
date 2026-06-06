using System.Data.SqlClient;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs;
using nexthire_api.Security;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

[ApiController]
[Route("api/teams")]
[Route("teams")] // alias: algunos clientes llaman /teams sin el prefijo /api
[Authorize]
public class TeamsController : ControllerBase
{
    private readonly ITeamService _teamService;
    private readonly ILogger<TeamsController> _logger;

    public TeamsController(ITeamService teamService, ILogger<TeamsController> logger)
    {
        _teamService = teamService;
        _logger = logger;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<TeamDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IEnumerable<TeamDto>>> GetTeams()
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var teams = await _teamService.GetTeamsAsync(orgId);
            return Ok(teams);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing teams");
            return StatusCode(500, new { message = "An error occurred while retrieving teams" });
        }
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(TeamDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TeamDto>> GetTeam(Guid id)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var team = await _teamService.GetTeamByIdAsync(orgId, id);
            if (team == null)
                return NotFound(new { message = $"Team with ID {id} not found" });

            return Ok(team);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting team {TeamId}", id);
            return StatusCode(500, new { message = "An error occurred while retrieving the team" });
        }
    }

    [HttpPost]
    [ProducesResponseType(typeof(TeamDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TeamDto>> CreateTeam([FromBody] CreateTeamRequestDto dto)
    {
        try
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var orgId = ClaimUtils.RequireOrgId(User);

            var (team, nameConflict) = await _teamService.CreateAsync(orgId, dto);
            if (nameConflict)
                return Conflict(new { message = "A team with this name already exists for your organization." });

            return CreatedAtAction(nameof(GetTeam), new { id = team!.Id }, team);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627)
        {
            return Conflict(new { message = "A team with this name already exists for your organization." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating team");
            return StatusCode(500, new { message = "An error occurred while creating the team" });
        }
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(TeamDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TeamDto>> UpdateTeam(Guid id, [FromBody] UpdateTeamRequestDto dto)
    {
        try
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var orgId = ClaimUtils.RequireOrgId(User);

            var (team, notFound, nameConflict) = await _teamService.UpdateAsync(orgId, id, dto);
            if (notFound)
                return NotFound(new { message = $"Team with ID {id} not found" });
            if (nameConflict)
                return Conflict(new { message = "A team with this name already exists for your organization." });

            return Ok(team);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627)
        {
            return Conflict(new { message = "A team with this name already exists for your organization." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating team {TeamId}", id);
            return StatusCode(500, new { message = "An error occurred while updating the team" });
        }
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteTeam(Guid id)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);

            var (deleted, notFound) = await _teamService.DeleteAsync(orgId, id);
            if (notFound)
                return NotFound(new { message = $"Team with ID {id} not found" });
            if (!deleted)
                return StatusCode(500, new { message = "Unable to delete team" });

            return NoContent();
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting team {TeamId}", id);
            return StatusCode(500, new { message = "An error occurred while deleting the team" });
        }
    }

    [HttpGet("{id:guid}/members")]
    [ProducesResponseType(typeof(IEnumerable<TeamMemberDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<TeamMemberDto>>> GetTeamMembers(Guid id)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);

            if (await _teamService.GetTeamByIdAsync(orgId, id) == null)
                return NotFound(new { message = $"Team with ID {id} not found" });

            var members = await _teamService.GetMembersAsync(orgId, id);
            return Ok(members);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing members for team {TeamId}", id);
            return StatusCode(500, new { message = "An error occurred while retrieving team members" });
        }
    }

    [HttpPost("{id:guid}/members")]
    [ProducesResponseType(typeof(TeamMemberDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TeamMemberDto>> AddTeamMember(Guid id, [FromBody] AddTeamMemberRequestDto dto)
    {
        try
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var orgId = ClaimUtils.RequireOrgId(User);

            var (member, teamNotFound, userNotFound, duplicate) = await _teamService.AddMemberAsync(orgId, id, dto);
            if (teamNotFound)
                return NotFound(new { message = $"Team with ID {id} not found" });
            if (userNotFound)
                return NotFound(new { message = $"User with ID {dto.UserId} not found" });
            if (duplicate)
                return Conflict(new { message = "This user is already a member of the team." });

            return CreatedAtAction(nameof(GetTeamMembers), new { id }, member);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627)
        {
            return Conflict(new { message = "This user is already a member of the team." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding team member for team {TeamId}", id);
            return StatusCode(500, new { message = "An error occurred while adding the team member" });
        }
    }

    [HttpDelete("{id:guid}/members/{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveTeamMember(Guid id, Guid userId)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);

            var (removed, teamNotFound, userNotFound, notMember) = await _teamService.RemoveMemberAsync(orgId, id, userId);
            if (teamNotFound)
                return NotFound(new { message = $"Team with ID {id} not found" });
            if (userNotFound)
                return NotFound(new { message = $"User with ID {userId} not found" });
            if (notMember)
                return NotFound(new { message = "Team membership was not found." });

            if (!removed)
                return StatusCode(500, new { message = "Unable to remove team member" });

            return NoContent();
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing team member for team {TeamId}, user {UserId}", id, userId);
            return StatusCode(500, new { message = "An error occurred while removing the team member" });
        }
    }
}
