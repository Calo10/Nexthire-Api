using System.Data.SqlClient;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs;
using nexthire_api.Services;
using System.Security.Claims;

namespace nexthire_api.Controllers;

[ApiController]
[Route("api/jobs")]
[Authorize]
public class JobsController : ControllerBase
{
    private readonly IJobService _jobService;
    private readonly ILogger<JobsController> _logger;

    public JobsController(IJobService jobService, ILogger<JobsController> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    /// <summary>
    /// Get all jobs
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<JobDto>>> GetJobs()
    {
        try
        {
            if (!TryGetOrgId(out var orgId, out var unauthorized))
                return unauthorized!;

            var jobs = await _jobService.GetAllJobsAsync(orgId);
            return Ok(jobs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting jobs");
            return StatusCode(500, "An error occurred while retrieving jobs");
        }
    }

    /// <summary>
    /// Get job by ID
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<JobDto>> GetJob(Guid id)
    {
        try
        {
            if (!TryGetOrgId(out var orgId, out var unauthorized))
                return unauthorized!;

            var job = await _jobService.GetJobByIdAsync(orgId, id);
            if (job == null)
            {
                return NotFound($"Job with ID {id} not found");
            }
            return Ok(job);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting job {JobId}", id);
            return StatusCode(500, "An error occurred while retrieving the job");
        }
    }

    /// <summary>
    /// Create a new job
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<JobDto>> CreateJob(CreateJobDto createJobDto)
    {
        try
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            if (!TryGetOrgId(out var orgId, out var unauthorized))
                return unauthorized!;

            if (!TryGetUserId(out var userId, out unauthorized))
                return unauthorized!;

            var job = await _jobService.CreateJobAsync(orgId, userId, createJobDto);
            return CreatedAtAction(nameof(GetJob), new { id = job.Id }, job);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating job");
            return StatusCode(500, "An error occurred while creating the job");
        }
    }

    /// <summary>
    /// Update an existing job
    /// </summary>
    [HttpPut("{id}")]
    public async Task<ActionResult<JobDto>> UpdateJob(Guid id, UpdateJobDto updateJobDto)
    {
        try
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            if (!TryGetOrgId(out var orgId, out var unauthorized))
                return unauthorized!;

            var job = await _jobService.UpdateJobAsync(orgId, id, updateJobDto);
            if (job == null)
            {
                return NotFound($"Job with ID {id} not found");
            }
            return Ok(job);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating job {JobId}", id);
            return StatusCode(500, "An error occurred while updating the job");
        }
    }

    /// <summary>
    /// Delete a job. Returns 409 if the job has associated applications.
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteJob(Guid id)
    {
        try
        {
            if (!TryGetOrgId(out var orgId, out var unauthorized))
                return unauthorized!;

            var (deleted, notFound, hasApplications) = await _jobService.DeleteJobAsync(orgId, id);
            if (notFound)
                return NotFound(new { message = $"Job with ID {id} not found" });
            if (hasApplications)
                return Conflict(new { message = "Cannot delete job because it has associated applications. Archive or remove applications first." });
            if (!deleted)
                return StatusCode(500, new { message = "Unable to delete job" });

            return NoContent();
        }
        catch (SqlException ex) when (ex.Number == 547)
        {
            return Conflict(new { message = "Cannot delete job because it is referenced by other records." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting job {JobId}", id);
            return StatusCode(500, new { message = "An error occurred while deleting the job" });
        }
    }

    private bool TryGetOrgId(out Guid orgId, out ActionResult? unauthorized)
    {
        unauthorized = null;
        orgId = Guid.Empty;

        var orgClaim = User.FindFirst("nexa_org_id")?.Value;
        if (string.IsNullOrWhiteSpace(orgClaim) || !Guid.TryParse(orgClaim, out orgId))
        {
            unauthorized = Unauthorized(new { message = "Missing org context. Login again." });
            return false;
        }

        return true;
    }

    private bool TryGetUserId(out Guid userId, out ActionResult? unauthorized)
    {
        unauthorized = null;
        userId = Guid.Empty;

        var userClaim = User.FindFirst("nexa_user_id")?.Value;
        if (string.IsNullOrWhiteSpace(userClaim) || !Guid.TryParse(userClaim, out userId))
        {
            unauthorized = Unauthorized(new { message = "Missing user context. Login again." });
            return false;
        }

        return true;
    }
}

