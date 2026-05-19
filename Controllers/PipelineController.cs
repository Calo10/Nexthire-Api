using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs;
using nexthire_api.Repositories;
using nexthire_api.Security;

namespace nexthire_api.Controllers;

[ApiController]
[Route("api/pipeline")]
[Authorize]
public class PipelineController : ControllerBase
{
    private readonly IPipelineRepository _pipelineRepository;
    private readonly ILogger<PipelineController> _logger;

    public PipelineController(IPipelineRepository pipelineRepository, ILogger<PipelineController> logger)
    {
        _pipelineRepository = pipelineRepository;
        _logger = logger;
    }

    /// <summary>
    /// Get pipeline stages for org. If none exist, creates defaults.
    /// </summary>
    [HttpGet("stages")]
    [ProducesResponseType(typeof(IEnumerable<PipelineStageDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IEnumerable<PipelineStageDto>>> GetStages([FromQuery] Guid? jobId = null)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);

            // jobId is optional; if provided, validate it belongs to org
            if (jobId.HasValue)
            {
                var ok = await _pipelineRepository.JobExistsInOrgAsync(orgId, jobId.Value);
                if (!ok)
                    return BadRequest(new { message = "jobId not found in this organization" });
            }

            var stages = await _pipelineRepository.EnsureDefaultStagesAsync(orgId);
            return Ok(stages);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting pipeline stages");
            return StatusCode(500, new { message = "An error occurred while retrieving pipeline stages" });
        }
    }
}

