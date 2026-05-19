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

    /// <summary>Build an AI image prompt from job title/description, generate a Meta-friendly square asset, return base64 for UI (no Meta upload).</summary>
    Task<GenerateMetaCreativePreviewResponse> GenerateCreativePreviewFromJobAsync(
        Guid orgId,
        Guid jobId,
        CancellationToken cancellationToken = default);

    Task<CreateMetaCampaignResponse> CreateCampaignAsync(
        Guid orgId,
        CreateMetaCampaignRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Graph API DELETE on the campaign node (removes campaign hierarchy in Meta).</summary>
    Task DeleteMetaCampaignNodeAsync(string metaCampaignId, CancellationToken cancellationToken = default);

    /// <summary>Deletes the remote Meta campaign then the local <c>marketing_meta_campaigns</c> row.</summary>
    Task DeleteMarketingCampaignRecordAsync(Guid orgId, Guid localRecordId, CancellationToken cancellationToken = default);
}
