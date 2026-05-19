using System.Data;
using nexthire_api.Data;
using nexthire_api.DTOs;
using nexthire_api.DTOs.Sourcing;
using nexthire_api.Repositories;
using nexthire_api.Repositories.Sourcing;
using nexthire_api.Services;
using nexthire_api.Sourcing;
using System.Text.Json;

namespace nexthire_api.Services.Sourcing;

public class SourcingService : ISourcingService
{
    private readonly ISourcingRepository _sourcing;
    private readonly ICandidateRepository _candidates;
    private readonly IApplicationsRepository _applications;
    private readonly IPipelineRepository _pipeline;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IMetaAdsService _metaAdsService;
    private readonly ILogger<SourcingService> _logger;

    public SourcingService(
        ISourcingRepository sourcing,
        ICandidateRepository candidates,
        IApplicationsRepository applications,
        IPipelineRepository pipeline,
        IDbConnectionFactory connectionFactory,
        IMetaAdsService metaAdsService,
        ILogger<SourcingService> logger)
    {
        _sourcing = sourcing;
        _candidates = candidates;
        _applications = applications;
        _pipeline = pipeline;
        _connectionFactory = connectionFactory;
        _metaAdsService = metaAdsService;
        _logger = logger;
    }

    public Task<SourcingDashboardDto> GetDashboardAsync(Guid orgId)
    {
        return _sourcing.GetDashboardAsync(orgId);
    }

    public Task<PagedResult<SourcingLeadListItemDto>> GetLeadsAsync(
        Guid orgId,
        Guid? jobId,
        Guid? campaignId,
        string? sourceTypeCode,
        string? status,
        string? search,
        string? availability,
        decimal? minFitScore,
        int page,
        int pageSize,
        string? sort,
        string? dir)
    {
        var (p, ps) = SourcingValidation.NormalizePaging(page, pageSize);
        if (!string.IsNullOrWhiteSpace(status))
            SourcingValidation.ValidateLeadStatus(status.Trim().ToLowerInvariant());
        SourcingValidation.ValidateFitScore(minFitScore);

        var sortField = string.IsNullOrWhiteSpace(sort) ? "created_at" : sort.Trim();
        var dirField = string.IsNullOrWhiteSpace(dir) ? "desc" : dir.Trim();

        return _sourcing.GetLeadsPagedAsync(
            orgId,
            jobId,
            campaignId,
            string.IsNullOrWhiteSpace(sourceTypeCode) ? null : sourceTypeCode.Trim(),
            string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            string.IsNullOrWhiteSpace(availability) ? null : availability.Trim(),
            minFitScore,
            p,
            ps,
            sortField,
            dirField);
    }

    public async Task<SourcingLeadDetailDto> CreateLeadAsync(Guid orgId, CreateSourcingLeadRequestDto dto)
    {
        SourcingValidation.EnsureLeadIdentity(dto);
        SourcingValidation.ValidateOptionalEmail(dto.Email);
        SourcingValidation.ValidateFitScore(dto.FitScore);

        var code = dto.SourceTypeCode.Trim();
        if (!await _sourcing.SourceTypeExistsAsync(code))
            throw new ArgumentException("sourceTypeCode does not exist.");

        if (dto.CampaignId.HasValue)
        {
            var c = await _sourcing.GetCampaignByIdAsync(orgId, dto.CampaignId.Value);
            if (c == null)
                throw new ArgumentException("campaignId not found for this organization.");
        }

        if (dto.JobId.HasValue && !await _pipeline.JobExistsInOrgAsync(orgId, dto.JobId.Value))
            throw new ArgumentException("jobId not found for this organization.");

        dto.SourceTypeCode = code;
        return await _sourcing.CreateLeadAsync(orgId, dto);
    }

    public Task<SourcingLeadDetailDto?> GetLeadAsync(Guid orgId, Guid leadId)
    {
        return _sourcing.GetLeadByIdAsync(orgId, leadId);
    }

    public async Task<SourcingLeadDetailDto?> UpdateLeadAsync(Guid orgId, Guid leadId, UpdateSourcingLeadRequestDto dto)
    {
        var existing = await _sourcing.GetLeadByIdAsync(orgId, leadId);
        if (existing == null)
            return null;

        SourcingValidation.ValidateOptionalEmail(dto.Email);
        SourcingValidation.ValidateFitScore(dto.FitScore);

        if (!string.IsNullOrWhiteSpace(dto.SourceTypeCode))
        {
            var code = dto.SourceTypeCode.Trim();
            if (!await _sourcing.SourceTypeExistsAsync(code))
                throw new ArgumentException("sourceTypeCode does not exist.");
            dto.SourceTypeCode = code;
        }

        if (dto.CampaignId.HasValue)
        {
            var c = await _sourcing.GetCampaignByIdAsync(orgId, dto.CampaignId.Value);
            if (c == null)
                throw new ArgumentException("campaignId not found for this organization.");
        }

        if (dto.JobId.HasValue && !await _pipeline.JobExistsInOrgAsync(orgId, dto.JobId.Value))
            throw new ArgumentException("jobId not found for this organization.");

        var merged = MergeLeadUpdate(existing, dto);
        return await _sourcing.UpdateLeadAsync(orgId, leadId, merged);
    }

    private static UpdateSourcingLeadRequestDto MergeLeadUpdate(SourcingLeadDetailDto ex, UpdateSourcingLeadRequestDto u)
    {
        return new UpdateSourcingLeadRequestDto
        {
            CampaignId = u.CampaignId ?? ex.CampaignId,
            JobId = u.JobId ?? ex.JobId,
            SourceTypeCode = u.SourceTypeCode ?? ex.SourceTypeCode,
            FirstName = u.FirstName ?? ex.FirstName,
            LastName = u.LastName ?? ex.LastName,
            FullName = u.FullName ?? ex.FullName,
            Email = u.Email ?? ex.Email,
            Phone = u.Phone ?? ex.Phone,
            ResumeUrl = u.ResumeUrl ?? ex.ResumeUrl,
            DesiredRole = u.DesiredRole ?? ex.DesiredRole,
            CurrentRole = u.CurrentRole ?? ex.CurrentRole,
            City = u.City ?? ex.City,
            State = u.State ?? ex.State,
            ZipCode = u.ZipCode ?? ex.ZipCode,
            Country = u.Country ?? ex.Country,
            Latitude = u.Latitude ?? ex.Latitude,
            Longitude = u.Longitude ?? ex.Longitude,
            Availability = u.Availability ?? ex.Availability,
            ExperienceYears = u.ExperienceYears ?? ex.ExperienceYears,
            EnglishLevel = u.EnglishLevel ?? ex.EnglishLevel,
            SpanishLevel = u.SpanishLevel ?? ex.SpanishLevel,
            HasTransportation = u.HasTransportation ?? ex.HasTransportation,
            WillingToRelocate = u.WillingToRelocate ?? ex.WillingToRelocate,
            FitScore = u.FitScore ?? ex.FitScore,
            QualificationNotes = u.QualificationNotes ?? ex.QualificationNotes,
            RawPayloadJson = u.RawPayloadJson ?? ex.RawPayloadJson
        };
    }

    public async Task<SourcingLeadDetailDto?> PatchLeadStatusAsync(Guid orgId, Guid leadId, PatchSourcingLeadStatusRequestDto dto)
    {
        var status = dto.Status.Trim().ToLowerInvariant();
        SourcingValidation.ValidateLeadStatus(status);

        // TODO: WhatsApp Business API — notify candidate when status becomes contacted (external integration).

        return await _sourcing.PatchLeadStatusAsync(orgId, leadId, status, dto.Notes);
    }

    public async Task<ConvertLeadToCandidateResponseDto?> ConvertLeadToCandidateAsync(
        Guid orgId,
        Guid leadId,
        Guid nexaUserId,
        string? userEmail,
        string? displayName)
    {
        var lead = await _sourcing.GetLeadByIdAsync(orgId, leadId);
        if (lead == null)
            return null;

        if (string.Equals(lead.Status, "converted", StringComparison.OrdinalIgnoreCase) && lead.ConvertedCandidateId.HasValue)
        {
            // Lead ya convertido pero sin aplicación al pipeline (p. ej. no había job_id antes, o fallo parcial).
            if (lead.JobId.HasValue && !lead.ConvertedApplicationId.HasValue)
            {
                _logger.LogInformation(
                    "Repairing converted lead {LeadId}: creating pipeline application for job {JobId} and candidate {CandidateId}.",
                    leadId,
                    lead.JobId,
                    lead.ConvertedCandidateId);

                await _pipeline.EnsureDefaultStagesAsync(orgId);
                var repairFirstStageId = await _pipeline.GetFirstStageIdAsync(orgId);
                if (!repairFirstStageId.HasValue)
                    throw new InvalidOperationException("Pipeline has no stages for this organization.");

                if (!await _pipeline.JobExistsInOrgAsync(orgId, lead.JobId.Value))
                    throw new ArgumentException("Lead jobId is not valid for this organization.");

                var repairNhUserId = await _applications.GetOrCreateNhUserIdAsync(orgId, nexaUserId, userEmail, displayName);
                var repairCandidateId = lead.ConvertedCandidateId.Value;

                using var repairConn = _connectionFactory.CreateConnection();
                repairConn.Open();
                using var repairTx = repairConn.BeginTransaction();
                try
                {
                    var existingApp = await _applications.FindApplicationIdForJobCandidateAsync(
                        orgId,
                        lead.JobId.Value,
                        repairCandidateId,
                        repairTx);
                    Guid repairApplicationId;
                    var repairApplicationCreated = false;
                    if (existingApp.HasValue)
                    {
                        repairApplicationId = existingApp.Value;
                    }
                    else
                    {
                        var card = await _applications.CreateKanbanAsync(
                            orgId,
                            lead.JobId.Value,
                            repairCandidateId,
                            repairFirstStageId.Value,
                            "active",
                            repairTx);
                        await _applications.InsertStageHistoryAsync(
                            orgId,
                            card.Id,
                            null,
                            repairFirstStageId.Value,
                            repairNhUserId,
                            repairTx);
                        repairApplicationId = card.Id;
                        repairApplicationCreated = true;
                    }

                    await _sourcing.UpdateLeadAfterConvertAsync(orgId, leadId, repairCandidateId, repairApplicationId, repairTx);
                    repairTx.Commit();

                    return new ConvertLeadToCandidateResponseDto
                    {
                        LeadId = leadId,
                        CandidateId = repairCandidateId,
                        ApplicationId = repairApplicationId,
                        CandidateCreated = false,
                        ApplicationCreated = repairApplicationCreated
                    };
                }
                catch
                {
                    repairTx.Rollback();
                    throw;
                }
            }

            return new ConvertLeadToCandidateResponseDto
            {
                LeadId = leadId,
                CandidateId = lead.ConvertedCandidateId.Value,
                ApplicationId = lead.ConvertedApplicationId,
                CandidateCreated = false,
                ApplicationCreated = false
            };
        }

        await _pipeline.EnsureDefaultStagesAsync(orgId);
        var firstStageId = await _pipeline.GetFirstStageIdAsync(orgId);
        if (!firstStageId.HasValue)
            throw new InvalidOperationException("Pipeline has no stages for this organization.");

        var nhUserId = await _applications.GetOrCreateNhUserIdAsync(orgId, nexaUserId, userEmail, displayName);

        var emailNorm = SourcingValidation.NormalizeEmail(lead.Email);
        var phoneDigits = SourcingValidation.NormalizePhoneDigits(lead.Phone);
        var resumeDocumentId = !string.IsNullOrWhiteSpace(lead.ResumeUrl)
            ? lead.ResumeUrl.Trim()
            : ExtractResumeDocumentIdFromRawPayload(lead.RawPayloadJson);

        CandidateDto? candidate = null;
        if (!string.IsNullOrEmpty(emailNorm))
            candidate = await _candidates.GetByEmailAsync(orgId, emailNorm);
        if (candidate == null && !string.IsNullOrEmpty(phoneDigits))
            candidate = await _candidates.GetByPhoneDigitsAsync(orgId, phoneDigits);

        var candidateCreated = false;
        Guid candidateId;
        Guid? applicationId = null;
        var applicationCreated = false;

        using var conn = _connectionFactory.CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();
        try
        {
            if (candidate == null)
            {
                var createDto = BuildCreateCandidateFromLead(lead, emailNorm, phoneDigits, resumeDocumentId);
                var emailLower = SourcingValidation.NormalizeEmail(createDto.Email);
                candidate = await _candidates.InsertAsync(orgId, createDto, emailLower, tx);
                candidateCreated = true;
            }
            else if (!string.IsNullOrWhiteSpace(resumeDocumentId))
            {
                var updatedCandidate = await _candidates.UpdateAsync(
                    orgId,
                    candidate.Id,
                    new UpdateCandidateRequestDto
                    {
                        FirstName = string.IsNullOrWhiteSpace(candidate.FirstName) ? "Unknown" : candidate.FirstName,
                        LastName = candidate.LastName ?? string.Empty,
                        Email = candidate.Email,
                        Phone = candidate.Phone,
                        Source = candidate.Source,
                        ResumeUrl = resumeDocumentId
                    },
                    candidate.Email.ToLowerInvariant());

                if (updatedCandidate != null)
                    candidate = updatedCandidate;
            }

            candidateId = candidate.Id;

            if (lead.JobId.HasValue)
            {
                if (!await _pipeline.JobExistsInOrgAsync(orgId, lead.JobId.Value))
                    throw new ArgumentException("Lead jobId is not valid for this organization.");

                var existingAppId = await _applications.FindApplicationIdForJobCandidateAsync(orgId, lead.JobId.Value, candidateId, tx);
                if (existingAppId.HasValue)
                {
                    applicationId = existingAppId;
                }
                else
                {
                    var card = await _applications.CreateKanbanAsync(orgId, lead.JobId.Value, candidateId, firstStageId.Value, "active", tx);
                    await _applications.InsertStageHistoryAsync(orgId, card.Id, null, firstStageId.Value, nhUserId, tx);
                    applicationId = card.Id;
                    applicationCreated = true;
                }
            }
            else
            {
                _logger.LogWarning(
                    "Convert lead {LeadId}: lead has no job_id; candidate created/updated but no pipeline application will exist.",
                    leadId);
            }

            await _sourcing.UpdateLeadAfterConvertAsync(orgId, leadId, candidateId, applicationId, tx);
            await _sourcing.InsertLeadEventAsync(orgId, leadId, "converted", notes: null, tx);

            tx.Commit();

            return new ConvertLeadToCandidateResponseDto
            {
                LeadId = leadId,
                CandidateId = candidateId,
                ApplicationId = applicationId,
                CandidateCreated = candidateCreated,
                ApplicationCreated = applicationCreated
            };
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static CreateCandidateRequestDto BuildCreateCandidateFromLead(
        SourcingLeadDetailDto lead,
        string emailNorm,
        string? phoneDigits,
        string? resumeDocumentId)
    {
        var first = !string.IsNullOrWhiteSpace(lead.FirstName)
            ? lead.FirstName!.Trim()
            : SplitFullName(lead.FullName, first: true);
        var last = !string.IsNullOrWhiteSpace(lead.LastName)
            ? lead.LastName!.Trim()
            : SplitFullName(lead.FullName, first: false);

        if (string.IsNullOrWhiteSpace(first))
            first = "Unknown";
        if (last == null)
            last = string.Empty;

        var email = !string.IsNullOrEmpty(emailNorm)
            ? emailNorm
            : $"lead-{Guid.NewGuid():N}@sourcing.placeholder.invalid";

        var phone = !string.IsNullOrWhiteSpace(lead.Phone)
            ? lead.Phone
            : (!string.IsNullOrEmpty(phoneDigits) ? phoneDigits : null);

        return new CreateCandidateRequestDto
        {
            FirstName = first,
            LastName = last,
            Email = email,
            Phone = phone,
            Source = string.IsNullOrWhiteSpace(lead.SourceTypeCode) ? "sourcing" : lead.SourceTypeCode,
            ResumeUrl = resumeDocumentId
        };
    }

    private static string? ExtractResumeDocumentIdFromRawPayload(string? rawPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(rawPayloadJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(rawPayloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            if (!doc.RootElement.TryGetProperty("resumeDocumentId", out var resumeNode))
                return null;

            var value = resumeNode.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string SplitFullName(string? fullName, bool first)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            return string.Empty;

        var parts = fullName.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return string.Empty;
        if (first)
            return parts[0];
        return parts.Length > 1 ? parts[1] : string.Empty;
    }

    public Task<IReadOnlyList<SourcingSourceTypeDto>> GetSourceTypesAsync()
    {
        return _sourcing.GetActiveSourceTypesAsync();
    }

    public Task<IReadOnlyList<SourcingSourceConnectionListItemDto>> GetSourceConnectionsAsync(Guid orgId)
    {
        return _sourcing.GetSourceConnectionsAsync(orgId);
    }

    public async Task UpsertSourceConnectionAsync(Guid orgId, UpsertSourcingSourceConnectionRequestDto dto)
    {
        var code = dto.SourceTypeCode.Trim();
        if (!await _sourcing.SourceTypeExistsAsync(code))
            throw new ArgumentException("sourceTypeCode does not exist.");

        dto.SourceTypeCode = code;
        await _sourcing.UpsertSourceConnectionAsync(orgId, dto);
    }

    public Task<PagedResult<SourcingCampaignListItemDto>> GetCampaignsAsync(
        Guid orgId,
        Guid? jobId,
        string? platform,
        string? status,
        string? search,
        int page,
        int pageSize)
    {
        var (p, ps) = SourcingValidation.NormalizePaging(page, pageSize);
        if (!string.IsNullOrWhiteSpace(status))
            SourcingValidation.ValidateCampaignStatus(status.Trim().ToLowerInvariant());

        return _sourcing.GetCampaignsPagedAsync(
            orgId,
            jobId,
            string.IsNullOrWhiteSpace(platform) ? null : platform.Trim(),
            string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            p,
            ps);
    }

    public async Task<SourcingCampaignDetailDto> CreateCampaignAsync(Guid orgId, CreateSourcingCampaignRequestDto dto)
    {
        if (dto.JobId.HasValue && !await _pipeline.JobExistsInOrgAsync(orgId, dto.JobId.Value))
            throw new ArgumentException("jobId not found for this organization.");

        if (!string.IsNullOrWhiteSpace(dto.Status))
            SourcingValidation.ValidateCampaignStatus(dto.Status.Trim().ToLowerInvariant());

        var created = await _sourcing.CreateCampaignAsync(orgId, dto);

        // TODO: Meta Ads API — create/link remote campaign when external ids are provided (integration).

        return created;
    }

    public Task<SourcingCampaignDetailDto?> GetCampaignAsync(Guid orgId, Guid campaignId)
    {
        return _sourcing.GetCampaignByIdAsync(orgId, campaignId);
    }

    public async Task<SourcingCampaignDetailDto?> UpdateCampaignAsync(Guid orgId, Guid campaignId, UpdateSourcingCampaignRequestDto dto)
    {
        if (dto.JobId.HasValue && !await _pipeline.JobExistsInOrgAsync(orgId, dto.JobId.Value))
            throw new ArgumentException("jobId not found for this organization.");

        return await _sourcing.UpdateCampaignAsync(orgId, campaignId, dto);
    }

    public async Task<SourcingCampaignDetailDto?> PatchCampaignStatusAsync(Guid orgId, Guid campaignId, PatchSourcingCampaignStatusRequestDto dto)
    {
        var status = dto.Status.Trim().ToLowerInvariant();
        SourcingValidation.ValidateCampaignStatus(status);

        var updated = await _sourcing.PatchCampaignStatusAsync(orgId, campaignId, status);

        if (updated != null && string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
        {
            // TODO: Meta Ads API — activate remote campaign when integration is available.
        }

        return updated;
    }

    public async Task<bool> DeleteCampaignAsync(Guid orgId, Guid campaignId)
    {
        var campaign = await _sourcing.GetCampaignByIdAsync(orgId, campaignId);
        if (campaign == null)
            return false;

        if (!string.IsNullOrWhiteSpace(campaign.ExternalCampaignId))
        {
            _logger.LogInformation(
                "Deleting remote Meta campaign before DB delete. OrgId={OrgId} CampaignId={CampaignId} MetaCampaignId={MetaCampaignId}",
                orgId,
                campaignId,
                campaign.ExternalCampaignId);

            await _metaAdsService.DeleteMetaCampaignNodeAsync(campaign.ExternalCampaignId!);
        }

        return await _sourcing.DeleteCampaignAsync(orgId, campaignId);
    }

    public async Task<SourcingTrackingEventCreatedDto> CreateTrackingEventAsync(Guid orgId, CreateSourcingTrackingEventRequestDto dto)
    {
        var eventType = dto.EventType.Trim().ToLowerInvariant();
        SourcingValidation.ValidateTrackingEventType(eventType);
        dto.EventType = eventType;

        if (!string.IsNullOrWhiteSpace(dto.SourceTypeCode))
        {
            var code = dto.SourceTypeCode.Trim();
            if (!await _sourcing.SourceTypeExistsAsync(code))
                throw new ArgumentException("sourceTypeCode does not exist.");
            dto.SourceTypeCode = code;
        }

        if (dto.CampaignId.HasValue && await _sourcing.GetCampaignByIdAsync(orgId, dto.CampaignId.Value) == null)
            throw new ArgumentException("campaignId not found for this organization.");

        if (dto.JobId.HasValue && !await _pipeline.JobExistsInOrgAsync(orgId, dto.JobId.Value))
            throw new ArgumentException("jobId not found for this organization.");

        if (dto.LeadId.HasValue && await _sourcing.GetLeadByIdAsync(orgId, dto.LeadId.Value) == null)
            throw new ArgumentException("leadId not found for this organization.");

        return await _sourcing.InsertTrackingEventAsync(orgId, dto);
    }
}
