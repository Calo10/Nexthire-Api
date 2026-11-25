using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs.AiAgents;
using nexthire_api.Security;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

[ApiController]
[Route("api/ai-agents")]
[Authorize]
public class AiAgentsController : ControllerBase
{
    private readonly IAiAgentService _agents;

    public AiAgentsController(IAiAgentService agents)
    {
        _agents = agents;
    }

    [HttpGet("catalog")]
    public async Task<ActionResult<IReadOnlyList<AgentCatalogItemDto>>> Catalog()
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            return Ok(await _agents.ListCatalogAsync(orgId));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<HiredAgentDto>>> ListHired()
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            return Ok(await _agents.ListHiredAsync(orgId));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
    }

    [HttpGet("usage")]
    public async Task<ActionResult<AgentTokenUsageDto>> Usage([FromQuery] int days = 90, CancellationToken cancellationToken = default)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            return Ok(await _agents.GetUsageAsync(orgId, days, cancellationToken));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
    }

    [HttpGet("{agentId}")]
    public async Task<ActionResult<HiredAgentDto>> GetHired(string agentId)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var item = await _agents.GetHiredAsync(orgId, agentId);
            return item is null ? NotFound(new { message = "Agent is not hired." }) : Ok(item);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{agentId}/hire")]
    public async Task<ActionResult<HiredAgentDto>> Hire(string agentId)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            Guid? hiredBy = null;
            try { hiredBy = ClaimUtils.RequireNexaUserId(User); } catch { /* optional */ }

            var (agent, created) = await _agents.HireAsync(orgId, agentId, hiredBy);
            if (!created) return Ok(agent);
            return CreatedAtAction(nameof(GetHired), new { agentId = agent.AgentId }, agent);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("{agentId}")]
    public async Task<IActionResult> Unhire(string agentId)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var deleted = await _agents.UnhireAsync(orgId, agentId);
            return deleted ? NoContent() : NotFound(new { message = "Agent is not hired." });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPatch("{agentId}/status")]
    public async Task<ActionResult<HiredAgentDto>> SetStatus(string agentId, [FromBody] UpdateAgentStatusRequestDto request)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var item = await _agents.SetStatusAsync(orgId, agentId, request.Status);
            return item is null ? NotFound(new { message = "Agent is not hired." }) : Ok(item);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPatch("{agentId}/context")]
    public async Task<ActionResult<HiredAgentDto>> SetContext(string agentId, [FromBody] UpdateAgentContextRequestDto request)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var item = await _agents.UpdateContextAsync(orgId, agentId, request);
            return item is null ? NotFound(new { message = "Agent is not hired." }) : Ok(item);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{agentId}/run")]
    public async Task<ActionResult<HiredAgentDto>> Run(string agentId, CancellationToken cancellationToken)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var nexaUserId = ClaimUtils.RequireNexaUserId(User);
            var email = ClaimUtils.GetEmail(User);
            var displayName = ClaimUtils.GetDisplayName(User);
            var item = await _agents.RunNowAsync(orgId, agentId, nexaUserId, email, displayName, cancellationToken);
            return item is null ? NotFound(new { message = "Agent is not hired." }) : Ok(item);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    [HttpGet("{agentId}/chat")]
    public async Task<ActionResult<AgentChatThreadDto>> ListChat(string agentId, CancellationToken cancellationToken)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var item = await _agents.ListChatAsync(orgId, agentId, cancellationToken);
            return item is null ? NotFound(new { message = "Agent is not hired." }) : Ok(item);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{agentId}/chat")]
    public async Task<ActionResult<AgentChatReplyDto>> Chat(
        string agentId,
        [FromBody] AgentChatRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var nexaUserId = ClaimUtils.RequireNexaUserId(User);
            var email = ClaimUtils.GetEmail(User);
            var displayName = ClaimUtils.GetDisplayName(User);
            var item = await _agents.ChatAsync(orgId, agentId, request, nexaUserId, email, displayName, cancellationToken);
            return item is null ? NotFound(new { message = "Agent is not hired." }) : Ok(item);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }
}
