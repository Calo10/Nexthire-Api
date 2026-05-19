using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Data.SqlClient;
using nexthire_api.DTOs;
using nexthire_api.Models.Public;
using nexthire_api.Repositories;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

[ApiController]
[Route("api/public/jobs")]
[AllowAnonymous]
public class PublicJobsController : ControllerBase
{
    private readonly IJobService _jobService;
    private readonly ICandidateRepository _candidateRepo;
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IApplicationsRepository _applicationsRepo;
    private readonly IResumeDocumentsUploader _resumeDocumentsUploader;
    private readonly ILogger<PublicJobsController> _logger;

    public PublicJobsController(
        IJobService jobService,
        ICandidateRepository candidateRepo,
        IPipelineRepository pipelineRepo,
        IApplicationsRepository applicationsRepo,
        IResumeDocumentsUploader resumeDocumentsUploader,
        ILogger<PublicJobsController> logger)
    {
        _jobService = jobService;
        _candidateRepo = candidateRepo;
        _pipelineRepo = pipelineRepo;
        _applicationsRepo = applicationsRepo;
        _resumeDocumentsUploader = resumeDocumentsUploader;
        _logger = logger;
    }

    /// <summary>
    /// Public: get jobs by organization (orgId in query).
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<JobDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IEnumerable<JobDto>>> GetPublicJobs([FromQuery] Guid orgId)
    {
        try
        {
            if (orgId == Guid.Empty)
                return BadRequest(new { message = "orgId is required" });

            var jobs = await _jobService.GetPublicOpenJobsAsync(orgId);
            return Ok(jobs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting public jobs for org {OrgId}", orgId);
            return StatusCode(500, new { message = "An error occurred while retrieving jobs" });
        }
    }

    /// <summary>
    /// Public: get a specific job by id and organization (orgId in query).
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(JobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<JobDto>> GetPublicJobById(Guid id, [FromQuery] Guid orgId)
    {
        try
        {
            if (orgId == Guid.Empty)
                return BadRequest(new { message = "orgId is required" });

            var job = await _jobService.GetPublicOpenJobByIdAsync(orgId, id);
            if (job is null)
                return NotFound(new { message = $"Job with ID {id} not found" });

            return Ok(job);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting public job {JobId} for org {OrgId}", id, orgId);
            return StatusCode(500, new { message = "An error occurred while retrieving the job" });
        }
    }

    /// <summary>
    /// Public: apply to a job (creates/reuses candidate in org and creates an application).
    /// </summary>
    [HttpPost("{jobId}/apply")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(ApplyJobResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApplyJobResponse>> ApplyToJob(Guid jobId, [FromQuery] Guid orgId, [FromForm] ApplyJobFormRequest request)
    {
        try
        {
            if (orgId == Guid.Empty)
                return BadRequest(new { message = "orgId is required" });
            if (jobId == Guid.Empty)
                return BadRequest(new { message = "jobId is required" });
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            // Only allow apply to open jobs (public jobs already enforce open).
            var job = await _jobService.GetPublicOpenJobByIdAsync(orgId, jobId);
            if (job is null)
                return NotFound(new { message = $"Job with ID {jobId} not found" });

            var emailLower = (request.Email ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(emailLower))
                return BadRequest(new { message = "email is required" });

            // Create or reuse candidate (unique by email per org).
            var candidate = await _candidateRepo.GetByEmailAsync(orgId, emailLower);

            // If candidate exists and already has an application for this job, short-circuit BEFORE uploading CV.
            if (candidate is not null)
            {
                var alreadyApplied = await _applicationsRepo.ExistsForJobCandidateAsync(orgId, jobId, candidate.Id);
                if (alreadyApplied)
                    return Conflict(new { message = "This candidate already has an application for the selected job." });
            }

            // Upload resume only after we know this is a new application attempt.
            // Store documentId (NOT originalUrl) in candidates.resume_url as requested.
            var resumeDocumentId = await _resumeDocumentsUploader.UploadResumeAsync(orgId, request.Resume, HttpContext.RequestAborted);

            if (candidate is null)
            {
                var created = await _candidateRepo.InsertAsync(
                    orgId,
                    new CreateCandidateRequestDto
                    {
                        FirstName = request.FirstName.Trim(),
                        LastName = request.LastName.Trim(),
                        Email = emailLower,
                        Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim(),
                        Source = string.IsNullOrWhiteSpace(request.Source) ? "public_apply" : request.Source.Trim(),
                        ResumeUrl = resumeDocumentId
                    },
                    emailLower);

                candidate = created;
            }
            else
            {
                // Update candidate with the new resume (and any provided fields).
                var updated = await _candidateRepo.UpdateAsync(
                    orgId,
                    candidate.Id,
                    new UpdateCandidateRequestDto
                    {
                        FirstName = request.FirstName.Trim(),
                        LastName = request.LastName.Trim(),
                        Email = emailLower,
                        Phone = string.IsNullOrWhiteSpace(request.Phone) ? candidate.Phone : request.Phone.Trim(),
                        Source = string.IsNullOrWhiteSpace(request.Source) ? candidate.Source : request.Source.Trim(),
                        ResumeUrl = resumeDocumentId
                    },
                    emailLower);

                if (updated is not null)
                    candidate = updated;
            }

            // Ensure default pipeline stages exist and use the first stage.
            await _pipelineRepo.EnsureDefaultStagesAsync(orgId);
            var firstStageId = await _pipelineRepo.GetFirstStageIdAsync(orgId);
            if (!firstStageId.HasValue)
                return StatusCode(500, new { message = "Pipeline stages are not configured for this organization" });

            // Create application (Kanban insert; no stage_history required for public apply).
            var application = await _applicationsRepo.CreateKanbanAsync(orgId, jobId, candidate.Id, firstStageId.Value, status: "active");

            var response = new ApplyJobResponse
            {
                Candidate = candidate,
                Application = application
            };

            return StatusCode(StatusCodes.Status201Created, response);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("upload", StringComparison.OrdinalIgnoreCase)
                                                   || ex.Message.Contains("Documents function", StringComparison.OrdinalIgnoreCase)
                                                   || ex.Message.Contains("endpoint", StringComparison.OrdinalIgnoreCase))
        {
            // Upstream dependency failure (Azure Function). Treat as 502 for FE clarity.
            return StatusCode(StatusCodes.Status502BadGateway, new { message = ex.Message });
        }
        catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627)
        {
            // Unique constraint likely (job_id + candidate_id)
            return Conflict(new { message = "This candidate already has an application for the selected job." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying to job {JobId} for org {OrgId}", jobId, orgId);
            return StatusCode(500, new { message = "An error occurred while applying to the job" });
        }
    }
}
