using nexthire_api.DTOs;
using nexthire_api.DTOs.Sourcing;

namespace nexthire_api.Services.Sourcing;

public interface ISourcingService
{
    Task<SourcingDashboardDto> GetDashboardAsync(Guid orgId);

    Task<PagedResult<SourcingLeadListItemDto>> GetLeadsAsync(
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
        string? dir);

    Task<SourcingLeadDetailDto> CreateLeadAsync(Guid orgId, CreateSourcingLeadRequestDto dto);

    Task<SourcingLeadDetailDto?> GetLeadAsync(Guid orgId, Guid leadId);

    Task<SourcingLeadDetailDto?> UpdateLeadAsync(Guid orgId, Guid leadId, UpdateSourcingLeadRequestDto dto);

    Task<SourcingLeadDetailDto?> PatchLeadStatusAsync(Guid orgId, Guid leadId, PatchSourcingLeadStatusRequestDto dto);

    Task<ConvertLeadToCandidateResponseDto?> ConvertLeadToCandidateAsync(
        Guid orgId,
        Guid leadId,
        Guid nexaUserId,
        string? userEmail,
        string? displayName);

    Task<IReadOnlyList<SourcingSourceTypeDto>> GetSourceTypesAsync();

    Task<IReadOnlyList<SourcingSourceConnectionListItemDto>> GetSourceConnectionsAsync(Guid orgId);

    Task UpsertSourceConnectionAsync(Guid orgId, UpsertSourcingSourceConnectionRequestDto dto);

    Task<PagedResult<SourcingCampaignListItemDto>> GetCampaignsAsync(
        Guid orgId,
        Guid? jobId,
        string? platform,
        string? status,
        string? search,
        int page,
        int pageSize);

    Task<SourcingCampaignDetailDto> CreateCampaignAsync(Guid orgId, CreateSourcingCampaignRequestDto dto);

    Task<SourcingCampaignDetailDto?> GetCampaignAsync(Guid orgId, Guid campaignId);

    Task<SourcingCampaignDetailDto?> UpdateCampaignAsync(Guid orgId, Guid campaignId, UpdateSourcingCampaignRequestDto dto);

    Task<SourcingCampaignDetailDto?> PatchCampaignStatusAsync(Guid orgId, Guid campaignId, PatchSourcingCampaignStatusRequestDto dto);

    Task<bool> DeleteCampaignAsync(Guid orgId, Guid campaignId);

    /// <summary>
    /// Inserts a tracking event. orgId may come from auth context or campaign resolution in the controller.
    /// </summary>
    Task<SourcingTrackingEventCreatedDto> CreateTrackingEventAsync(Guid orgId, CreateSourcingTrackingEventRequestDto dto);
}
