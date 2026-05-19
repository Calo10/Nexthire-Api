using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Data.SqlClient;
using System.Security.Claims;
using nexthire_api.DTOs;
using nexthire_api.Models.Public;
using nexthire_api.Repositories;
using nexthire_api.Security;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

[ApiController]
[Route("api/candidates")]
[Authorize]
public class CandidatesController : ControllerBase
{
    private readonly ICandidateService _candidateService;
    private readonly ICandidateRepository _candidateRepo;
    private readonly IDocumentService _documentService;
    private readonly IResumeDocumentsUploader _resumeDocumentsUploader;
    private readonly IApplicationsService _applicationsService;
    private readonly ILogger<CandidatesController> _logger;

    public CandidatesController(
        ICandidateService candidateService,
        ICandidateRepository candidateRepo,
        IDocumentService documentService,
        IResumeDocumentsUploader resumeDocumentsUploader,
        IApplicationsService applicationsService,
        ILogger<CandidatesController> logger)
    {
        _candidateService = candidateService;
        _candidateRepo = candidateRepo;
        _documentService = documentService;
        _resumeDocumentsUploader = resumeDocumentsUploader;
        _applicationsService = applicationsService;
        _logger = logger;
    }

    private void LogClaimsShapeForDebug()
    {
        // Safe: log claim TYPE NAMES only (no values, no token).
        var claimTypes = User.Claims
            .Select(c => c.Type)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x)
            .ToArray();

        var hasOrgId = User.HasClaim(c => c.Type == "org_id");
        var hasOrgIdAlt = User.HasClaim(c => c.Type == "orgId");

        var hasSub = User.HasClaim(c => c.Type == "sub");
        var hasNameId = User.HasClaim(c => c.Type == ClaimTypes.NameIdentifier);

        _logger.LogInformation(
            "Candidates auth claims snapshot. ClaimTypes: [{ClaimTypes}]. HasOrgId: {HasOrgId}, HasOrgIdAlt: {HasOrgIdAlt}, HasSub: {HasSub}, HasNameIdentifier: {HasNameIdentifier}",
            string.Join(", ", claimTypes),
            hasOrgId,
            hasOrgIdAlt,
            hasSub,
            hasNameId);
    }

    /// <summary>
    /// Get paged candidates for the current org
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<CandidateListItemDto>>> GetCandidates(
        [FromQuery] string? search,
        [FromQuery] string? source,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? sort = "created_at",
        [FromQuery] string? dir = "desc")
    {
        try
        {
            if (page < 1)
                return BadRequest(new { message = "page must be >= 1" });
            if (pageSize < 1 || pageSize > 200)
                return BadRequest(new { message = "pageSize must be between 1 and 200" });
            if (from.HasValue && to.HasValue && from.Value > to.Value)
                return BadRequest(new { message = "from must be <= to" });

            var orgId = ClaimUtils.RequireOrgId(User);
            _ = ClaimUtils.RequireUserId(User);

            var result = await _candidateService.GetPagedAsync(orgId, search, source, from, to, page, pageSize, sort, dir);
            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogClaimsShapeForDebug();
            return Unauthorized(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting candidates");
            return StatusCode(500, new { message = "An error occurred while retrieving candidates" });
        }
    }

    /// <summary>
    /// Get candidate by ID (org-scoped)
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<CandidateDto>> GetCandidate(Guid id)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            _ = ClaimUtils.RequireUserId(User);

            var candidate = await _candidateService.GetByIdAsync(orgId, id);
            if (candidate == null)
                return NotFound(new { message = $"Candidate with ID {id} not found" });

            return Ok(candidate);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogClaimsShapeForDebug();
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting candidate {CandidateId}", id);
            return StatusCode(500, new { message = "An error occurred while retrieving the candidate" });
        }
    }

    /// <summary>
    /// Create a new candidate (org-scoped)
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<CandidateDto>> CreateCandidate([FromBody] CreateCandidateRequestDto dto)
    {
        try
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var orgId = ClaimUtils.RequireOrgId(User);
            var userId = ClaimUtils.RequireUserId(User);

            var (candidate, emailConflict) = await _candidateService.CreateAsync(orgId, userId, dto);
            if (emailConflict)
                return Conflict(new { message = "A candidate with this email already exists for your organization." });

            return CreatedAtAction(nameof(GetCandidate), new { id = candidate!.Id }, candidate);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogClaimsShapeForDebug();
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating candidate");
            return StatusCode(500, new { message = "An error occurred while creating the candidate" });
        }
    }

    /// <summary>
    /// Create a candidate with resume upload (multipart). Uses the same Documents function pipeline and field names as public job apply (FirstName, LastName, Email, Phone, Source, Resume).
    /// Duplicate email in the organization returns 409 Conflict (same as JSON POST /api/candidates); the file is not uploaded if the email already exists.
    /// </summary>
    [HttpPost("from-apply-form")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(CandidateDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<CandidateDto>> CreateCandidateFromApplyForm([FromForm] ApplyJobFormRequest request)
    {
        try
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var orgId = ClaimUtils.RequireOrgId(User);
            var userId = ClaimUtils.RequireUserId(User);

            var emailLower = (request.Email ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(emailLower))
                return BadRequest(new { message = "email is required" });

            var exists = await _candidateRepo.ExistsByEmailAsync(orgId, emailLower);
            if (exists)
                return Conflict(new { message = "A candidate with this email already exists for your organization." });

            var resumeDocumentId = await _resumeDocumentsUploader.UploadResumeAsync(orgId, request.Resume, HttpContext.RequestAborted);

            var dto = new CreateCandidateRequestDto
            {
                FirstName = request.FirstName.Trim(),
                LastName = request.LastName.Trim(),
                Email = emailLower,
                Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim(),
                Source = string.IsNullOrWhiteSpace(request.Source) ? "panel_apply" : request.Source.Trim(),
                ResumeUrl = resumeDocumentId
            };

            var (candidate, emailConflict) = await _candidateService.CreateAsync(orgId, userId, dto);
            if (emailConflict)
                return Conflict(new { message = "A candidate with this email already exists for your organization." });

            return CreatedAtAction(nameof(GetCandidate), new { id = candidate!.Id }, candidate);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogClaimsShapeForDebug();
            return Unauthorized(new { message = ex.Message });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("upload", StringComparison.OrdinalIgnoreCase)
                                                   || ex.Message.Contains("Documents function", StringComparison.OrdinalIgnoreCase)
                                                   || ex.Message.Contains("endpoint", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = ex.Message });
        }
        catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627)
        {
            return Conflict(new { message = "A candidate with this email already exists for your organization." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating candidate from apply form");
            return StatusCode(500, new { message = "An error occurred while creating the candidate" });
        }
    }

    /// <summary>
    /// Create an application linking this candidate to a job (same rules as POST /api/applications: first pipeline stage unless currentStageId is set, initial stage history row, 409 if duplicate job+candidate).
    /// </summary>
    [HttpPost("{id}/applications")]
    [ProducesResponseType(typeof(ApplicationCardDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApplicationCardDto>> CreateApplicationForCandidate(Guid id, [FromBody] CreateApplicationForCandidateRequest request)
    {
        try
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);
            if (id == Guid.Empty)
                return BadRequest(new { message = "candidate id is required" });

            var orgId = ClaimUtils.RequireOrgId(User);
            var nexaUserId = ClaimUtils.RequireNexaUserId(User);
            var email = ClaimUtils.GetEmail(User);
            var displayName = ClaimUtils.GetDisplayName(User);

            var candidate = await _candidateService.GetByIdAsync(orgId, id);
            if (candidate == null)
                return NotFound(new { message = $"Candidate with ID {id} not found" });

            var createdCard = await _applicationsService.CreateKanbanApplicationAsync(
                orgId,
                nexaUserId,
                email,
                displayName,
                request.JobId,
                id,
                request.CurrentStageId,
                request.Status);

            return CreatedAtAction(
                nameof(ApplicationsController.GetApplicationKanbanDetail),
                "Applications",
                new { id = createdCard.Id },
                createdCard);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogClaimsShapeForDebug();
            return Unauthorized(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(500, new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627)
        {
            return Conflict(new { message = "This candidate already has an application for the selected job." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating application for candidate {CandidateId}", id);
            return StatusCode(500, new { message = "An error occurred while creating the application" });
        }
    }

    /// <summary>
    /// Update an existing candidate (org-scoped)
    /// </summary>
    [HttpPut("{id}")]
    public async Task<ActionResult<CandidateDto>> UpdateCandidate(Guid id, [FromBody] UpdateCandidateRequestDto dto)
    {
        try
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var orgId = ClaimUtils.RequireOrgId(User);
            var userId = ClaimUtils.RequireUserId(User);

            var (candidate, notFound, emailConflict) = await _candidateService.UpdateAsync(orgId, userId, id, dto);
            if (notFound)
                return NotFound(new { message = $"Candidate with ID {id} not found" });
            if (emailConflict)
                return Conflict(new { message = "A candidate with this email already exists for your organization." });

            return Ok(candidate);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogClaimsShapeForDebug();
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating candidate {CandidateId}", id);
            return StatusCode(500, new { message = "An error occurred while updating the candidate" });
        }
    }

    /// <summary>
    /// Delete a candidate (org-scoped)
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteCandidate(Guid id)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var userId = ClaimUtils.RequireUserId(User);

            var (deleted, notFound, hasApplications) = await _candidateService.DeleteAsync(orgId, userId, id);
            if (notFound)
                return NotFound(new { message = $"Candidate with ID {id} not found" });
            if (hasApplications)
                return Conflict(new { message = "Cannot delete candidate because they are referenced by existing applications." });
            if (!deleted)
                return StatusCode(500, new { message = "Unable to delete candidate" });

            return NoContent();
        }
        catch (UnauthorizedAccessException ex)
        {
            LogClaimsShapeForDebug();
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting candidate {CandidateId}", id);
            return StatusCode(500, new { message = "An error occurred while deleting the candidate" });
        }
    }

    /// <summary>
    /// Get a signed download URL for a candidate's resume (org-scoped).
    /// </summary>
    [HttpGet("{candidateId}/resume/download-url")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status504GatewayTimeout)]
    public async Task<IActionResult> GetResumeDownloadUrl(Guid candidateId, CancellationToken ct)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            _ = ClaimUtils.RequireUserId(User);

            // Distinguish 404 vs 403
            var ownerOrgId = await _candidateRepo.GetOrgIdByCandidateIdAsync(candidateId);
            if (!ownerOrgId.HasValue)
                return NotFound(new { message = $"Candidate with ID {candidateId} not found" });
            if (ownerOrgId.Value != orgId)
                return StatusCode(StatusCodes.Status403Forbidden, new { message = "You do not have access to this candidate." });

            var candidate = await _candidateService.GetByIdAsync(orgId, candidateId);
            if (candidate is null)
                return NotFound(new { message = $"Candidate with ID {candidateId} not found" });

            var rawDocId = candidate.ResumeUrl;
            if (string.IsNullOrWhiteSpace(rawDocId))
                return NotFound(new { message = "Candidate resume not found." });
            if (!Guid.TryParse(rawDocId, out var documentId))
                return StatusCode(500, new { message = "Candidate resume document id is invalid." });

            _logger.LogInformation("Resume download-url requested. OrgId: {OrgId}, CandidateId: {CandidateId}, DocumentId: {DocumentId}",
                orgId, candidateId, documentId);

            var url = await _documentService.GetResumeDownloadUrlAsync(orgId, documentId, ct);

            // Do NOT log signed URL
            return Ok(new { downloadUrl = url });
        }
        catch (UnauthorizedAccessException ex)
        {
            LogClaimsShapeForDebug();
            return Unauthorized(new { message = ex.Message });
        }
        catch (DocumentServiceException ex)
        {
            _logger.LogWarning("DocumentService error. Kind: {Kind}, UpstreamStatus: {UpstreamStatus}",
                ex.Kind, ex.UpstreamStatusCode);

            return ex.Kind switch
            {
                DocumentServiceErrorKind.NotFound => NotFound(new { message = "Resume document not found." }),
                DocumentServiceErrorKind.UpstreamUnauthorized => StatusCode(StatusCodes.Status502BadGateway, new { message = "Resume service unauthorized." }),
                DocumentServiceErrorKind.Timeout => StatusCode(StatusCodes.Status504GatewayTimeout, new { message = "Resume service timeout." }),
                _ => StatusCode(500, new { message = "An error occurred while retrieving resume download URL" })
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting resume download-url for candidate {CandidateId}", candidateId);
            return StatusCode(500, new { message = "An error occurred while retrieving resume download URL" });
        }
    }

    /// <summary>
    /// Get (or trigger) AI analysis for a candidate's resume document (org-scoped).
    /// </summary>
    [HttpGet("{candidateId}/resume/analysis")]
    [ProducesResponseType(typeof(DocumentAnalysisEnvelopeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status504GatewayTimeout)]
    public async Task<IActionResult> GetResumeAnalysis(Guid candidateId, CancellationToken ct)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            _ = ClaimUtils.RequireUserId(User);

            // Distinguish 404 vs 403
            var ownerOrgId = await _candidateRepo.GetOrgIdByCandidateIdAsync(candidateId);
            if (!ownerOrgId.HasValue)
                return NotFound(new { message = $"Candidate with ID {candidateId} not found" });
            if (ownerOrgId.Value != orgId)
                return StatusCode(StatusCodes.Status403Forbidden, new { message = "You do not have access to this candidate." });

            var candidate = await _candidateService.GetByIdAsync(orgId, candidateId);
            if (candidate is null)
                return NotFound(new { message = $"Candidate with ID {candidateId} not found" });

            var rawDocId = candidate.ResumeUrl; // stores documentId
            if (string.IsNullOrWhiteSpace(rawDocId))
                return NotFound(new { message = "Candidate resume not found." });
            if (!Guid.TryParse(rawDocId, out var documentId))
                return StatusCode(500, new { message = "Candidate resume document id is invalid." });

            _logger.LogInformation("Resume analysis requested. OrgId: {OrgId}, CandidateId: {CandidateId}, DocumentId: {DocumentId}",
                orgId, candidateId, documentId);

            var analysis = await _documentService.GetResumeAnalysisAsync(orgId, documentId, ct);
            return Ok(analysis);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogClaimsShapeForDebug();
            return Unauthorized(new { message = ex.Message });
        }
        catch (DocumentServiceException ex)
        {
            _logger.LogWarning("DocumentService error (analysis). Kind: {Kind}, UpstreamStatus: {UpstreamStatus}",
                ex.Kind, ex.UpstreamStatusCode);

            return ex.Kind switch
            {
                DocumentServiceErrorKind.NotFound => NotFound(new { message = "Resume document not found." }),
                DocumentServiceErrorKind.UpstreamUnauthorized => StatusCode(StatusCodes.Status502BadGateway, new { message = "Resume service unauthorized." }),
                DocumentServiceErrorKind.Timeout => StatusCode(StatusCodes.Status504GatewayTimeout, new { message = "Resume service timeout." }),
                _ => StatusCode(500, new { message = "An error occurred while retrieving resume analysis" })
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting resume analysis for candidate {CandidateId}", candidateId);
            return StatusCode(500, new { message = "An error occurred while retrieving resume analysis" });
        }
    }
}

