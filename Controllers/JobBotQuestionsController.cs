using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs;
using nexthire_api.Security;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

[ApiController]
[Route("api/jobs/{jobId:guid}/bot-questions")]
[Authorize]
public class JobBotQuestionsController : ControllerBase
{
    private readonly IJobBotQuestionService _service;
    private readonly ILogger<JobBotQuestionsController> _logger;

    public JobBotQuestionsController(IJobBotQuestionService service, ILogger<JobBotQuestionsController> logger)
    {
        _service = service;
        _logger = logger;
    }

    /// <summary>
    /// List custom bot questions configured for a job.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<JobBotQuestionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<JobBotQuestionDto>>> List(
        Guid jobId,
        [FromQuery] bool includeInactive = false)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var items = await _service.ListAsync(orgId, jobId, includeInactive);
            return Ok(items);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing bot questions for job {JobId}", jobId);
            return StatusCode(500, new { message = "An error occurred while retrieving bot questions" });
        }
    }

    /// <summary>
    /// Get one bot question by id.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(JobBotQuestionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<JobBotQuestionDto>> Get(Guid jobId, Guid id)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var item = await _service.GetAsync(orgId, jobId, id);
            if (item == null)
                return NotFound(new { message = $"Bot question with ID {id} not found for job {jobId}" });

            return Ok(item);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting bot question {QuestionId} for job {JobId}", id, jobId);
            return StatusCode(500, new { message = "An error occurred while retrieving the bot question" });
        }
    }

    /// <summary>
    /// Create a custom bot question for a job.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(JobBotQuestionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<JobBotQuestionDto>> Create(Guid jobId, [FromBody] CreateJobBotQuestionRequestDto request)
    {
        try
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var orgId = ClaimUtils.RequireOrgId(User);
            var created = await _service.CreateAsync(orgId, jobId, request);
            return CreatedAtAction(nameof(Get), new { jobId, id = created.Id }, created);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating bot question for job {JobId}", jobId);
            return StatusCode(500, new { message = "An error occurred while creating the bot question" });
        }
    }

    /// <summary>
    /// Update a custom bot question. questionKey is immutable after create.
    /// </summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(JobBotQuestionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<JobBotQuestionDto>> Update(
        Guid jobId,
        Guid id,
        [FromBody] UpdateJobBotQuestionRequestDto request)
    {
        try
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var orgId = ClaimUtils.RequireOrgId(User);
            var updated = await _service.UpdateAsync(orgId, jobId, id, request);
            if (updated == null)
                return NotFound(new { message = $"Bot question with ID {id} not found for job {jobId}" });

            return Ok(updated);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating bot question {QuestionId} for job {JobId}", id, jobId);
            return StatusCode(500, new { message = "An error occurred while updating the bot question" });
        }
    }

    /// <summary>
    /// Delete a custom bot question.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid jobId, Guid id)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var deleted = await _service.DeleteAsync(orgId, jobId, id);
            if (!deleted)
                return NotFound(new { message = $"Bot question with ID {id} not found for job {jobId}" });

            return NoContent();
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting bot question {QuestionId} for job {JobId}", id, jobId);
            return StatusCode(500, new { message = "An error occurred while deleting the bot question" });
        }
    }
}
