using System.Data;
using nexthire_api.Data;
using nexthire_api.DTOs;
using nexthire_api.DTOs.Sourcing;
using nexthire_api.Helpers;
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
    private readonly IWhatsAppMessengerRouteSyncService _whatsAppMessengerRouteSync;
    private readonly ISourcingLeadFitScoringAgent _fitScoringAgent;
    private readonly ILogger<SourcingService> _logger;

    public SourcingService(
        ISourcingRepository sourcing,
        ICandidateRepository candidates,
        IApplicationsRepository applications,
        IPipelineRepository pipeline,
        IDbConnectionFactory connectionFactory,
        IMetaAdsService metaAdsService,
        IWhatsAppMessengerRouteSyncService whatsAppMessengerRouteSync,
        ISourcingLeadFitScoringAgent fitScoringAgent,
        ILogger<SourcingService> logger)
    {
        _sourcing = sourcing;
        _candidates = candidates;
        _applications = applications;
        _pipeline = pipeline;
        _connectionFactory = connectionFactory;
        _metaAdsService = metaAdsService;
        _whatsAppMessengerRouteSync = whatsAppMessengerRouteSync;
        _fitScoringAgent = fitScoringAgent;
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

    public Task ScoreLeadFitAsync(
        Guid orgId,
        SourcingLeadDetailDto lead,
        Task<string?>? resumeSummaryTask = null,
        CancellationToken cancellationToken = default)
    {
        return ScoreLeadFitCoreAsync(orgId, lead, resumeSummaryTask, cancellationToken);
    }

    public Task<string?> PrefetchResumeSummaryAsync(
        Guid orgId,
        string? resumeDocumentId,
        CancellationToken cancellationToken = default)
        => _fitScoringAgent.FetchResumeSummaryAsync(orgId, resumeDocumentId, cancellationToken);

    public Task<bool> LeadExistsForJobAndEmailAsync(Guid orgId, Guid jobId, string emailLower)
        => _sourcing.LeadExistsForJobAndEmailAsync(orgId, jobId, emailLower);

    private async Task ScoreLeadFitCoreAsync(
        Guid orgId,
        SourcingLeadDetailDto lead,
        Task<string?>? resumeSummaryTask,
        CancellationToken cancellationToken)
    {
        var resumeSummary = resumeSummaryTask is not null
            ? await resumeSummaryTask.ConfigureAwait(false)
            : null;

        await _fitScoringAgent.ScoreLeadIfEnabledAsync(orgId, lead, resumeSummary, cancellationToken);
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
            DynamicAnswersJson = u.DynamicAnswersJson ?? ex.DynamicAnswersJson,
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

        Guid candidateId;
        Guid? applicationId = null;
        var applicationCreated = false;

        using var conn = _connectionFactory.CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();
        try
        {
            // Always create a new candidate from the lead. WhatsApp leads share the sender phone
            // number, so matching by phone (or email) would incorrectly reuse unrelated candidates.
            var createDto = BuildCreateCandidateFromLead(lead, emailNorm, phoneDigits, resumeDocumentId);
            var emailLower = SourcingValidation.NormalizeEmail(createDto.Email);
            if (await _candidates.ExistsByEmailAsync(orgId, emailLower))
            {
                _logger.LogWarning(
                    "Convert lead {LeadId}: email {Email} already exists; using lead-specific placeholder email for new candidate.",
                    leadId,
                    emailLower);
                emailLower = $"lead-{leadId:N}@sourcing.placeholder.invalid";
                createDto.Email = emailLower;
            }

            var candidate = await _candidates.InsertAsync(orgId, createDto, emailLower, tx);
            candidateId = candidate.Id;
            var candidateCreated = true;

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

    public async Task<UpsertSourcingSourceConnectionResponseDto> UpsertSourceConnectionAsync(
        Guid orgId,
        UpsertSourcingSourceConnectionRequestDto dto,
        CancellationToken cancellationToken = default)
    {
        var code = dto.SourceTypeCode.Trim();
        if (!await _sourcing.SourceTypeExistsAsync(code))
            throw new ArgumentException("sourceTypeCode does not exist.");

        dto.SourceTypeCode = code;

        if (string.Equals(code, "twilio", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(dto.ConfigJson))
        {
            var existingConfig = await _sourcing.GetSourceConnectionConfigJsonAsync(orgId, code, cancellationToken);
            dto.ConfigJson = TwilioSourceConnectionConfigMerger.Merge(dto.ConfigJson, existingConfig);
        }

        await _sourcing.UpsertSourceConnectionAsync(orgId, dto);

        var routeSync = await _whatsAppMessengerRouteSync.SyncTwilioRouteAfterUpsertAsync(orgId, dto, cancellationToken);
        if (routeSync is not null)
            return routeSync;

        return new UpsertSourcingSourceConnectionResponseDto
        {
            SourceTypeCode = code
        };
    }

    public async Task<PagedResult<SourcingCampaignListItemDto>> GetCampaignsAsync(
        Guid orgId,
        Guid? jobId,
        string? platform,
        string? status,
        string? search,
        int page,
        int pageSize)
    {
        await _metaAdsService.SyncMarketingCampaignsToSourcingAsync(orgId);

        var (p, ps) = SourcingValidation.NormalizePaging(page, pageSize);
        if (!string.IsNullOrWhiteSpace(status))
            SourcingValidation.ValidateCampaignStatus(status.Trim().ToLowerInvariant());

        return await _sourcing.GetCampaignsPagedAsync(
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

    public async Task<SourcingCampaignDetailDto?> GetCampaignAsync(Guid orgId, Guid campaignId)
    {
        var campaign = await _sourcing.GetCampaignByIdAsync(orgId, campaignId);
        if (campaign != null)
        {
            await AttachMetaCreativeImageAsync(orgId, campaign, campaignId);
            return campaign;
        }

        var meta = await _metaAdsService.GetMarketingCampaignAsync(orgId, campaignId);
        if (meta == null)
            return null;

        return MapMetaMarketingCampaignToSourcingDetail(meta);
    }

    private async Task AttachMetaCreativeImageAsync(
        Guid orgId,
        SourcingCampaignDetailDto campaign,
        Guid campaignId)
    {
        var platform = campaign.Platform?.Trim() ?? string.Empty;
        var isMeta =
            platform.Equals("meta", StringComparison.OrdinalIgnoreCase) ||
            platform.Equals("meta_ads", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(campaign.ExternalCampaignId);
        if (!isMeta)
            return;

        // Synced Meta rows share the same id as marketing_meta_campaigns.
        // GetMarketingCampaignAsync backfills image_base64 from Meta when missing.
        var meta = await _metaAdsService.GetMarketingCampaignAsync(orgId, campaignId);
        if (meta == null || string.IsNullOrWhiteSpace(meta.ImageBase64))
            return;

        campaign.ImageBase64 = meta.ImageBase64;
        campaign.ImageContentType = meta.ImageContentType;
    }

    private static SourcingCampaignDetailDto MapMetaMarketingCampaignToSourcingDetail(MetaMarketingCampaignDto meta) =>
        new()
        {
            Id = meta.Id,
            JobId = meta.JobId,
            Name = meta.CampaignName,
            Platform = "meta",
            Status = MapMetaStatusToSourcingStatus(meta.Status),
            Currency = SourcingConstants.DefaultCurrency,
            LandingPageUrl = meta.DestinationUrl,
            ExternalCampaignId = meta.MetaCampaignId,
            ImageBase64 = meta.ImageBase64,
            ImageContentType = meta.ImageContentType,
            CreatedAt = meta.CreatedAtUtc,
            UpdatedAt = meta.UpdatedAtUtc
        };

    private static string MapMetaStatusToSourcingStatus(string metaStatus)
    {
        if (string.Equals(metaStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            return "active";
        if (string.Equals(metaStatus, "PAUSED", StringComparison.OrdinalIgnoreCase))
            return "paused";
        return metaStatus.Trim().ToLowerInvariant();
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

        var campaign = await _sourcing.GetCampaignByIdAsync(orgId, campaignId);
        if (campaign == null)
            return null;

        var isMetaLinked =
            string.Equals(campaign.Platform, "meta", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(campaign.ExternalCampaignId);

        if (isMetaLinked)
        {
            var campaignRef = campaignId.ToString("D");
            if (string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
                await _metaAdsService.ActivateMarketingCampaignAsync(orgId, campaignRef);
            else if (string.Equals(status, "paused", StringComparison.OrdinalIgnoreCase))
                await _metaAdsService.PauseMarketingCampaignAsync(orgId, campaignRef);
            else
                await _sourcing.PatchCampaignStatusAsync(orgId, campaignId, status);

            return await _sourcing.GetCampaignByIdAsync(orgId, campaignId);
        }

        return await _sourcing.PatchCampaignStatusAsync(orgId, campaignId, status);
    }

    public async Task<bool> DeleteCampaignAsync(Guid orgId, Guid campaignId)
    {
        var campaign = await _sourcing.GetCampaignByIdAsync(orgId, campaignId);
        if (campaign == null)
            return false;

        var isMetaLinked =
            string.Equals(campaign.Platform, "meta", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(campaign.ExternalCampaignId);

        if (isMetaLinked)
        {
            _logger.LogInformation(
                "Deleting Meta-linked campaign. OrgId={OrgId} CampaignId={CampaignId} MetaCampaignId={MetaCampaignId}",
                orgId,
                campaignId,
                campaign.ExternalCampaignId);

            try
            {
                await _metaAdsService.DeleteMarketingCampaignRecordAsync(
                    orgId,
                    campaignId.ToString("D"));
                return true;
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(
                    ex,
                    "No marketing_meta_campaigns row for {CampaignId}; deleting remote Meta object and sourcing row.",
                    campaignId);

                if (!string.IsNullOrWhiteSpace(campaign.ExternalCampaignId))
                {
                    await _metaAdsService.DeleteMetaCampaignNodeAsync(
                        orgId,
                        campaign.ExternalCampaignId);
                }
            }
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
