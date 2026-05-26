using System.Data;
using nexthire_api.DTOs;
using nexthire_api.DTOs.Sourcing;

namespace nexthire_api.Repositories.Sourcing;

public interface ISourcingRepository
{
    Task<SourcingDashboardDto> GetDashboardAsync(Guid orgId);

    Task<PagedResult<SourcingLeadListItemDto>> GetLeadsPagedAsync(
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
        string sort,
        string dir);

    Task<SourcingLeadDetailDto?> GetLeadByIdAsync(Guid orgId, Guid leadId);

    Task<SourcingLeadDetailDto> CreateLeadAsync(Guid orgId, CreateSourcingLeadRequestDto dto);

    Task<SourcingLeadDetailDto?> UpdateLeadAsync(Guid orgId, Guid leadId, UpdateSourcingLeadRequestDto dto);

    Task<SourcingLeadDetailDto?> PatchLeadStatusAsync(Guid orgId, Guid leadId, string newStatus, string? notes);

    Task<bool> SourceTypeExistsAsync(string code);

    Task<IReadOnlyList<SourcingSourceTypeDto>> GetActiveSourceTypesAsync();

    Task<IReadOnlyList<SourcingSourceConnectionListItemDto>> GetSourceConnectionsAsync(Guid orgId);

    Task UpsertSourceConnectionAsync(Guid orgId, UpsertSourcingSourceConnectionRequestDto dto);

    /// <summary>
    /// Active Meta connection JSON from <c>sourcing_source_connections.config_json</c>, or null if not connected.
    /// </summary>
    Task<string?> GetActiveSourceConnectionConfigJsonAsync(
        Guid orgId,
        string sourceTypeCode,
        CancellationToken cancellationToken = default);

    Task<PagedResult<SourcingCampaignListItemDto>> GetCampaignsPagedAsync(
        Guid orgId,
        Guid? jobId,
        string? platform,
        string? status,
        string? search,
        int page,
        int pageSize);

    Task EnsureCampaignsSchemaAsync(CancellationToken cancellationToken = default);

    Task<SourcingCampaignDetailDto> CreateCampaignAsync(Guid orgId, CreateSourcingCampaignRequestDto dto);

    /// <summary>Inserts or updates a Meta-linked row in <c>sourcing_campaigns</c> using a fixed id (matches <c>marketing_meta_campaigns</c>).</summary>
    Task<SourcingCampaignDetailDto> UpsertMetaAdsCampaignAsync(
        Guid orgId,
        Guid campaignId,
        Guid jobId,
        string name,
        string sourcingStatus,
        decimal? dailyBudget,
        string? landingPageUrl,
        string metaCampaignId,
        string adAccountId,
        CancellationToken cancellationToken = default);

    /// <summary>Inserts a Meta-linked row only if missing (does not overwrite status on list sync).</summary>
    Task EnsureMetaAdsCampaignRowAsync(
        Guid orgId,
        Guid campaignId,
        Guid jobId,
        string name,
        string sourcingStatus,
        decimal? dailyBudget,
        string? landingPageUrl,
        string metaCampaignId,
        string adAccountId,
        CancellationToken cancellationToken = default);

    Task<SourcingCampaignDetailDto?> GetCampaignByIdAsync(Guid orgId, Guid campaignId);

    Task<Guid?> GetCampaignOrgIdAsync(Guid campaignId);

    Task<SourcingCampaignDetailDto?> UpdateCampaignAsync(Guid orgId, Guid campaignId, UpdateSourcingCampaignRequestDto dto);

    Task<SourcingCampaignDetailDto?> PatchCampaignStatusAsync(Guid orgId, Guid campaignId, string status);

    Task<bool> DeleteCampaignAsync(Guid orgId, Guid campaignId);

    Task<SourcingTrackingEventCreatedDto> InsertTrackingEventAsync(Guid orgId, CreateSourcingTrackingEventRequestDto dto);

    Task UpdateLeadAfterConvertAsync(
        Guid orgId,
        Guid leadId,
        Guid candidateId,
        Guid? applicationId,
        IDbTransaction transaction);

    Task InsertLeadEventAsync(
        Guid orgId,
        Guid leadId,
        string eventType,
        string? notes,
        IDbTransaction? transaction);
}
