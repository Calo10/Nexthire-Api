using Microsoft.AspNetCore.Http;
using nexthire_api.DTOs;

namespace nexthire_api.Services;

public interface IMetaAdsService
{
    /// <summary>Upload a creative image to Meta (multipart file from the client).</summary>
    Task<UploadMetaImageResponse> UploadCreativeImageAsync(
        Guid orgId,
        IFormFile file,
        CancellationToken cancellationToken = default);

    /// <summary>Build an AI image prompt from job title/description (+ optional ad text / AI instructions), generate a Meta-friendly square asset, return base64 for UI (no Meta upload).</summary>
    Task<GenerateMetaCreativePreviewResponse> GenerateCreativePreviewFromJobAsync(
        Guid orgId,
        Guid jobId,
        string? creativeMessage = null,
        string? aiInstructions = null,
        CancellationToken cancellationToken = default);

    Task<CreateMetaCampaignResponse> CreateCampaignAsync(
        Guid orgId,
        CreateMetaCampaignRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MetaMarketingCampaignDto>> ListMarketingCampaignsAsync(
        Guid orgId,
        Guid? jobId = null,
        CancellationToken cancellationToken = default);

    Task<MetaMarketingCampaignDto?> GetMarketingCampaignAsync(
        Guid orgId,
        Guid localRecordId,
        CancellationToken cancellationToken = default);

    /// <summary>Ensures rows in <c>marketing_meta_campaigns</c> exist in <c>sourcing_campaigns</c> (same id).</summary>
    Task SyncMarketingCampaignsToSourcingAsync(
        Guid orgId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolve a human location label into Meta geo keys (for ad set targeting).
    /// </summary>
    Task<ResolveMetaGeoResponse> ResolveGeoAsync(
        Guid orgId,
        ResolveMetaGeoRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Graph API DELETE on the campaign node (removes campaign hierarchy in Meta).</summary>
    Task DeleteMetaCampaignNodeAsync(
        Guid orgId,
        string metaCampaignId,
        CancellationToken cancellationToken = default);

    /// <summary>Pauses campaign, ad set, and ad in Meta and updates the local record status.</summary>
    /// <param name="campaignRef">Local record GUID or Meta <c>campaignId</c> from create response.</param>
    Task<MetaMarketingCampaignActionResponse> PauseMarketingCampaignAsync(
        Guid orgId,
        string campaignRef,
        CancellationToken cancellationToken = default);

    /// <summary>Activates campaign, ad set, and ad in Meta and updates local records.</summary>
    /// <param name="campaignRef">Local record GUID or Meta <c>campaignId</c> from create response.</param>
    Task<MetaMarketingCampaignActionResponse> ActivateMarketingCampaignAsync(
        Guid orgId,
        string campaignRef,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes the remote Meta campaign then the local <c>marketing_meta_campaigns</c> row.</summary>
    /// <param name="campaignRef">Local record GUID or Meta <c>campaignId</c> from create response.</param>
    Task DeleteMarketingCampaignRecordAsync(
        Guid orgId,
        string campaignRef,
        CancellationToken cancellationToken = default);

    /// <summary>Live Meta Insights for one campaign (local GUID or Meta campaign id).</summary>
    Task<MetaCampaignInsightsDto> GetCampaignInsightsAsync(
        Guid orgId,
        string campaignRef,
        string? datePreset = null,
        CancellationToken cancellationToken = default);

    /// <summary>Live Meta Insights for all org Meta campaigns (table summary).</summary>
    Task<MetaCampaignInsightsListResponse> ListCampaignInsightsAsync(
        Guid orgId,
        string? datePreset = null,
        CancellationToken cancellationToken = default);

    /// <summary>Live Meta Ad Account status (active / payment issues / disabled).</summary>
    Task<MetaAdAccountStatusDto> GetAdAccountStatusAsync(
        Guid orgId,
        CancellationToken cancellationToken = default);
}
