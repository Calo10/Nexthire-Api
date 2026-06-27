using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs;
using nexthire_api.DTOs.Sourcing;
using nexthire_api.Repositories.Sourcing;
using nexthire_api.Security;
using nexthire_api.Services;
using nexthire_api.Services.Sourcing;

namespace nexthire_api.Controllers;

[ApiController]
[Route("api/sourcing")]
public class SourcingController : ControllerBase
{
    private readonly ISourcingService _sourcing;
    private readonly ISourcingRepository _sourcingRepository;
    private readonly IResumeDocumentsUploader _resumeDocumentsUploader;
    private readonly ILogger<SourcingController> _logger;

    public SourcingController(
        ISourcingService sourcing,
        ISourcingRepository sourcingRepository,
        IResumeDocumentsUploader resumeDocumentsUploader,
        ILogger<SourcingController> logger)
    {
        _sourcing = sourcing;
        _sourcingRepository = sourcingRepository;
        _resumeDocumentsUploader = resumeDocumentsUploader;
        _logger = logger;
    }

    /// <summary>
    /// Sourcing KPIs and recent activity for the current organization.
    /// </summary>
    [HttpGet("dashboard")]
    [Authorize]
    public async Task<ActionResult<SourcingDashboardDto>> GetDashboard()
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var dto = await _sourcing.GetDashboardAsync(orgId);
            return Ok(dto);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading sourcing dashboard");
            return StatusCode(500, new { message = "An error occurred while loading the sourcing dashboard." });
        }
    }

    [HttpGet("leads")]
    [Authorize]
    public async Task<ActionResult<PagedResult<SourcingLeadListItemDto>>> GetLeads(
        [FromQuery] Guid? jobId,
        [FromQuery] Guid? campaignId,
        [FromQuery] string? sourceTypeCode,
        [FromQuery] string? status,
        [FromQuery] string? search,
        [FromQuery] string? availability,
        [FromQuery] decimal? minFitScore,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? sort = "created_at",
        [FromQuery] string? dir = "desc")
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var result = await _sourcing.GetLeadsAsync(
                orgId, jobId, campaignId, sourceTypeCode, status, search, availability, minFitScore,
                page, pageSize, sort, dir);
            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing sourcing leads");
            return StatusCode(500, new { message = "An error occurred while listing leads." });
        }
    }

    [HttpPost("leads")]
    [AllowAnonymous]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<SourcingLeadDetailDto>> CreateLead([FromQuery] Guid? orgId, [FromForm] CreateSourcingLeadFormRequestDto form)
    {
        try
        {
            var resolvedOrgId = orgId ?? form.OrgId;
            if (!resolvedOrgId.HasValue || resolvedOrgId.Value == Guid.Empty)
                return BadRequest(new { message = "orgId is required" });
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var resumeDocumentId = await _resumeDocumentsUploader.UploadResumeAsync(resolvedOrgId.Value, form.Resume, HttpContext.RequestAborted);
            var dto = MapCreateLeadFormToDto(form);
            dto.ResumeUrl = resumeDocumentId;
            var created = await _sourcing.CreateLeadAsync(resolvedOrgId.Value, dto);
            return CreatedAtAction(nameof(GetLead), new { id = created.Id }, created);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("upload", StringComparison.OrdinalIgnoreCase)
                                                   || ex.Message.Contains("Documents function", StringComparison.OrdinalIgnoreCase)
                                                   || ex.Message.Contains("endpoint", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Documents service unavailable while creating sourcing lead");
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                message = "Resume upload service is unavailable. Please try again in a moment."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating sourcing lead");
            return StatusCode(500, new { message = "An error occurred while creating the lead." });
        }
    }

    [HttpPost("leads/json")]
    [AllowAnonymous]
    [Consumes("application/json")]
    public ActionResult<SourcingLeadDetailDto> CreateLeadJson([FromBody] CreateSourcingLeadRequestDto _)
    {
        return BadRequest(new
        {
            message = "This endpoint requires multipart/form-data with the Resume file in the 'Resume' field."
        });
    }

    [HttpGet("leads/{id:guid}")]
    [Authorize]
    public async Task<ActionResult<SourcingLeadDetailDto>> GetLead(Guid id)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var lead = await _sourcing.GetLeadAsync(orgId, id);
            if (lead == null)
                return NotFound();
            return Ok(lead);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading sourcing lead {LeadId}", id);
            return StatusCode(500, new { message = "An error occurred while loading the lead." });
        }
    }

    [HttpPut("leads/{id:guid}")]
    [Authorize]
    public async Task<ActionResult<SourcingLeadDetailDto>> UpdateLead(Guid id, [FromBody] UpdateSourcingLeadRequestDto dto)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var updated = await _sourcing.UpdateLeadAsync(orgId, id, dto);
            if (updated == null)
                return NotFound();
            return Ok(updated);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating sourcing lead {LeadId}", id);
            return StatusCode(500, new { message = "An error occurred while updating the lead." });
        }
    }

    [HttpPatch("leads/{id:guid}/status")]
    [Authorize]
    public async Task<ActionResult<SourcingLeadDetailDto>> PatchLeadStatus(Guid id, [FromBody] PatchSourcingLeadStatusRequestDto dto)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var updated = await _sourcing.PatchLeadStatusAsync(orgId, id, dto);
            if (updated == null)
                return NotFound();
            return Ok(updated);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error patching lead status {LeadId}", id);
            return StatusCode(500, new { message = "An error occurred while updating lead status." });
        }
    }

    [HttpPost("leads/{id:guid}/convert-to-candidate")]
    [Authorize]
    public async Task<ActionResult<ConvertLeadToCandidateResponseDto>> ConvertLeadToCandidate(Guid id)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var nexaUserId = ClaimUtils.RequireNexaUserId(User);
            var email = ClaimUtils.GetEmail(User);
            var displayName = ClaimUtils.GetDisplayName(User);

            var result = await _sourcing.ConvertLeadToCandidateAsync(orgId, id, nexaUserId, email, displayName);
            if (result == null)
                return NotFound();
            return Ok(result);
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error converting lead {LeadId} to candidate", id);
            return StatusCode(500, new { message = "An error occurred while converting the lead." });
        }
    }

    [HttpGet("source-types")]
    [Authorize]
    public async Task<ActionResult<IReadOnlyList<SourcingSourceTypeDto>>> GetSourceTypes()
    {
        try
        {
            _ = ClaimUtils.RequireOrgId(User);
            var items = await _sourcing.GetSourceTypesAsync();
            return Ok(items);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing sourcing source types");
            return StatusCode(500, new { message = "An error occurred while listing source types." });
        }
    }

    [HttpGet("source-connections")]
    [Authorize]
    public async Task<ActionResult<IReadOnlyList<SourcingSourceConnectionListItemDto>>> GetSourceConnections()
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var items = await _sourcing.GetSourceConnectionsAsync(orgId);
            return Ok(items);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing source connections");
            return StatusCode(500, new { message = "An error occurred while listing source connections." });
        }
    }

    [HttpPost("source-connections")]
    [Authorize]
    public async Task<ActionResult<UpsertSourcingSourceConnectionResponseDto>> UpsertSourceConnection(
        [FromBody] UpsertSourcingSourceConnectionRequestDto dto,
        CancellationToken cancellationToken)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var result = await _sourcing.UpsertSourceConnectionAsync(orgId, dto, cancellationToken);
            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error upserting source connection");
            return StatusCode(500, new { message = "An error occurred while saving the source connection." });
        }
    }

    [HttpGet("campaigns")]
    [Authorize]
    public async Task<ActionResult<PagedResult<SourcingCampaignListItemDto>>> GetCampaigns(
        [FromQuery] Guid? jobId,
        [FromQuery] string? platform,
        [FromQuery] string? status,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var result = await _sourcing.GetCampaignsAsync(orgId, jobId, platform, status, search, page, pageSize);
            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing sourcing campaigns");
            return StatusCode(500, new { message = "An error occurred while listing campaigns." });
        }
    }

    [HttpPost("campaigns")]
    [Authorize]
    public async Task<ActionResult<SourcingCampaignDetailDto>> CreateCampaign([FromBody] CreateSourcingCampaignRequestDto dto)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var created = await _sourcing.CreateCampaignAsync(orgId, dto);
            return CreatedAtAction(nameof(GetCampaign), new { id = created.Id }, created);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating sourcing campaign");
            return StatusCode(500, new { message = "An error occurred while creating the campaign." });
        }
    }

    [HttpGet("campaigns/{id:guid}")]
    [Authorize]
    public async Task<ActionResult<SourcingCampaignDetailDto>> GetCampaign(Guid id)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var campaign = await _sourcing.GetCampaignAsync(orgId, id);
            if (campaign == null)
                return NotFound();
            return Ok(campaign);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading campaign {CampaignId}", id);
            return StatusCode(500, new { message = "An error occurred while loading the campaign." });
        }
    }

    [HttpPut("campaigns/{id:guid}")]
    [Authorize]
    public async Task<ActionResult<SourcingCampaignDetailDto>> UpdateCampaign(Guid id, [FromBody] UpdateSourcingCampaignRequestDto dto)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var updated = await _sourcing.UpdateCampaignAsync(orgId, id, dto);
            if (updated == null)
                return NotFound();
            return Ok(updated);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating campaign {CampaignId}", id);
            return StatusCode(500, new { message = "An error occurred while updating the campaign." });
        }
    }

    [HttpPatch("campaigns/{id:guid}/status")]
    [Authorize]
    public async Task<ActionResult<SourcingCampaignDetailDto>> PatchCampaignStatus(Guid id, [FromBody] PatchSourcingCampaignStatusRequestDto dto)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var updated = await _sourcing.PatchCampaignStatusAsync(orgId, id, dto);
            if (updated == null)
                return NotFound();
            return Ok(updated);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error patching campaign status {CampaignId}", id);
            return StatusCode(500, new { message = "An error occurred while updating campaign status." });
        }
    }

    [HttpDelete("campaigns/{id:guid}")]
    [Authorize]
    public async Task<IActionResult> DeleteCampaign(Guid id)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var deleted = await _sourcing.DeleteCampaignAsync(orgId, id);
            if (!deleted)
                return NotFound();
            return NoContent();
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (MetaGraphApiException ex)
        {
            return StatusCode(502, new
            {
                message = "Meta deletion failed. Campaign was not removed from database.",
                metaError = ex.Message,
                metaCode = ex.MetaCode
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting campaign {CampaignId}", id);
            return StatusCode(500, new { message = "An error occurred while deleting the campaign." });
        }
    }

    /// <summary>
    /// Records an analytics / funnel event. Use JWT for authenticated calls.
    /// For anonymous tracking (e.g. public landing page), pass campaignId so the server can resolve org_id.
    /// </summary>
    [HttpPost("tracking-events")]
    [AllowAnonymous]
    public async Task<ActionResult<SourcingTrackingEventCreatedDto>> CreateTrackingEvent([FromBody] CreateSourcingTrackingEventRequestDto dto)
    {
        try
        {
            var orgResolution = await ResolveOrgIdForTrackingAsync(dto);
            if (orgResolution.ErrorResult != null)
                return orgResolution.ErrorResult;

            var created = await _sourcing.CreateTrackingEventAsync(orgResolution.OrgId!.Value, dto);
            return Ok(created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating sourcing tracking event");
            return StatusCode(500, new { message = "An error occurred while recording the event." });
        }
    }

    private async Task<(Guid? OrgId, ActionResult? ErrorResult)> ResolveOrgIdForTrackingAsync(CreateSourcingTrackingEventRequestDto dto)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            try
            {
                var orgFromJwt = ClaimUtils.RequireOrgId(User);

                if (dto.CampaignId.HasValue)
                {
                    var campaignOrg = await _sourcingRepository.GetCampaignOrgIdAsync(dto.CampaignId.Value);
                    if (!campaignOrg.HasValue)
                        return (null, NotFound(new { message = "Campaign not found." }));

                    if (campaignOrg.Value != orgFromJwt)
                        return (null, BadRequest(new { message = "campaignId does not belong to your organization." }));
                }

                return (orgFromJwt, null);
            }
            catch (UnauthorizedAccessException ex)
            {
                return (null, Unauthorized(new { message = ex.Message }));
            }
        }

        if (!dto.CampaignId.HasValue)
            return (null, BadRequest(new { message = "campaignId is required when the request is not authenticated." }));

        var orgId = await _sourcingRepository.GetCampaignOrgIdAsync(dto.CampaignId.Value);
        if (!orgId.HasValue)
            return (null, NotFound(new { message = "Campaign not found." }));

        return (orgId, null);
    }

    private static CreateSourcingLeadRequestDto MapCreateLeadFormToDto(CreateSourcingLeadFormRequestDto form)
    {
        return new CreateSourcingLeadRequestDto
        {
            CampaignId = form.CampaignId,
            JobId = form.JobId,
            SourceTypeCode = form.SourceTypeCode,
            FirstName = form.FirstName,
            LastName = form.LastName,
            FullName = form.FullName,
            Email = form.Email,
            Phone = form.Phone,
            DesiredRole = form.DesiredRole,
            CurrentRole = form.CurrentRole,
            City = form.City,
            State = form.State,
            ZipCode = form.ZipCode,
            Country = form.Country,
            Latitude = form.Latitude,
            Longitude = form.Longitude,
            Availability = form.Availability,
            ExperienceYears = form.ExperienceYears,
            EnglishLevel = form.EnglishLevel,
            SpanishLevel = form.SpanishLevel,
            HasTransportation = form.HasTransportation,
            WillingToRelocate = form.WillingToRelocate,
            FitScore = form.FitScore,
            QualificationNotes = form.QualificationNotes,
            RawPayloadJson = form.RawPayloadJson
        };
    }
}
