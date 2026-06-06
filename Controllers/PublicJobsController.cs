using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Data.SqlClient;
using nexthire_api.DTOs;
using nexthire_api.Models.Public;
using nexthire_api.DTOs.Sourcing;
using nexthire_api.Helpers;
using nexthire_api.Repositories;
using nexthire_api.Services;
using nexthire_api.Services.Sourcing;

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
    private readonly IJobBotQuestionService _jobBotQuestions;
    private readonly ISourcingService _sourcing;
    private readonly ILogger<PublicJobsController> _logger;

    public PublicJobsController(
        IJobService jobService,
        ICandidateRepository candidateRepo,
        IPipelineRepository pipelineRepo,
        IApplicationsRepository applicationsRepo,
        IResumeDocumentsUploader resumeDocumentsUploader,
        IJobBotQuestionService jobBotQuestions,
        ISourcingService sourcing,
        ILogger<PublicJobsController> logger)
    {
        _jobService = jobService;
        _candidateRepo = candidateRepo;
        _pipelineRepo = pipelineRepo;
        _applicationsRepo = applicationsRepo;
        _resumeDocumentsUploader = resumeDocumentsUploader;
        _jobBotQuestions = jobBotQuestions;
        _sourcing = sourcing;
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
    [ProducesResponseType(typeof(PublicJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PublicJobDto>> GetPublicJobById(Guid id, [FromQuery] Guid orgId)
    {
        try
        {
            if (orgId == Guid.Empty)
                return BadRequest(new { message = "orgId is required" });

            var job = await _jobService.GetPublicOpenJobByIdAsync(orgId, id);
            if (job is null)
                return NotFound(new { message = $"Job with ID {id} not found" });

            var botQuestions = await _jobBotQuestions.ListAsync(orgId, id, includeInactive: false);
            return Ok(PublicJobDto.FromJob(job, botQuestions));
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
    public async Task<ActionResult<ApplyJobResponse>> ApplyToJob(
        Guid jobId,
        [FromQuery] Guid orgId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (orgId == Guid.Empty)
                return BadRequest(new { message = "orgId is required" });
            if (jobId == Guid.Empty)
                return BadRequest(new { message = "jobId is required" });

            var (form, parseError) = PublicJobApplyFormParser.Parse(Request.Form, Request.Form.Files);
            if (parseError is not null)
                return BadRequest(new { message = parseError });
            if (form is null || form.Resume is null)
                return BadRequest(new { message = "Resume or answerFile_{questionId} is required." });

            var job = await _jobService.GetPublicOpenJobByIdAsync(orgId, jobId);
            if (job is null)
                return NotFound(new { message = $"Job with ID {jobId} not found" });

            var emailLower = form.Email.Trim().ToLowerInvariant();
            var botQuestions = await _jobBotQuestions.ListAsync(orgId, jobId, includeInactive: false);

            var candidate = await _candidateRepo.GetByEmailAsync(orgId, emailLower);
            if (candidate is not null)
            {
                var alreadyApplied = await _applicationsRepo.ExistsForJobCandidateAsync(orgId, jobId, candidate.Id);
                if (alreadyApplied)
                    return Conflict(new { message = "This candidate already has an application for the selected job." });
            }

            string? dynamicAnswersJson;
            string resumeDocumentId;
            try
            {
                (dynamicAnswersJson, resumeDocumentId) = await PublicJobApplyFormParser.ProcessApplyFilesAsync(
                    orgId,
                    form.DynamicAnswersJson,
                    botQuestions,
                    form.AnswerFilesByQuestionId,
                    form.Resume,
                    _resumeDocumentsUploader,
                    cancellationToken);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }

            if (!string.IsNullOrWhiteSpace(dynamicAnswersJson))
            {
                await _sourcing.CreateLeadAsync(
                    orgId,
                    new CreateSourcingLeadRequestDto
                    {
                        JobId = jobId,
                        SourceTypeCode = form.SourceTypeCode,
                        FirstName = form.FirstName,
                        LastName = form.LastName,
                        FullName = string.IsNullOrWhiteSpace(form.FullName) ? null : form.FullName,
                        Email = emailLower,
                        Phone = form.Phone,
                        ResumeUrl = resumeDocumentId,
                        DynamicAnswersJson = dynamicAnswersJson,
                        RawPayloadJson = JsonSerializer.Serialize(new { channel = "public_web", jobId })
                    });
            }

            if (candidate is null)
            {
                candidate = await _candidateRepo.InsertAsync(
                    orgId,
                    new CreateCandidateRequestDto
                    {
                        FirstName = form.FirstName,
                        LastName = form.LastName,
                        Email = emailLower,
                        Phone = form.Phone,
                        Source = string.IsNullOrWhiteSpace(form.Source) ? "public_apply" : form.Source,
                        ResumeUrl = resumeDocumentId
                    },
                    emailLower);
            }
            else
            {
                var updated = await _candidateRepo.UpdateAsync(
                    orgId,
                    candidate.Id,
                    new UpdateCandidateRequestDto
                    {
                        FirstName = form.FirstName,
                        LastName = form.LastName,
                        Email = emailLower,
                        Phone = form.Phone ?? candidate.Phone,
                        Source = string.IsNullOrWhiteSpace(form.Source) ? candidate.Source : form.Source,
                        ResumeUrl = resumeDocumentId
                    },
                    emailLower);

                if (updated is not null)
                    candidate = updated;
            }

            await _pipelineRepo.EnsureDefaultStagesAsync(orgId);
            var firstStageId = await _pipelineRepo.GetFirstStageIdAsync(orgId);
            if (!firstStageId.HasValue)
                return StatusCode(500, new { message = "Pipeline stages are not configured for this organization" });

            var application = await _applicationsRepo.CreateKanbanAsync(
                orgId,
                jobId,
                candidate.Id,
                firstStageId.Value,
                status: "active");

            return StatusCode(StatusCodes.Status201Created, new ApplyJobResponse
            {
                Candidate = candidate,
                Application = application
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
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
