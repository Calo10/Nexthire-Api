using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using nexthire_api.DTOs.Meta;

namespace nexthire_api.DTOs;

/// <summary>Multipart body for POST /api/marketing/meta/creative-image (Swagger-compatible).</summary>
public class UploadMetaCreativeImageForm
{
    [Required]
    public IFormFile File { get; set; } = default!;
}

/// <summary>Body for POST /api/marketing/meta/creative-image/demo — AI preview from job copy.</summary>
public class GenerateMetaCreativePreviewRequest
{
    [Required]
    public Guid JobId { get; set; }

    /// <summary>Optional ad primary text shown on the Meta creative.</summary>
    public string? CreativeMessage { get; set; }

    /// <summary>Optional free-form instructions that steer the AI image generation.</summary>
    public string? AiInstructions { get; set; }
}

/// <summary>Generated ad image for UI preview (not uploaded to Meta until the client calls creative-image).</summary>
public class GenerateMetaCreativePreviewResponse
{
    public string ImageBase64 { get; set; } = string.Empty;
    public string ContentType { get; set; } = "image/png";
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>How this size maps to typical Meta feed / square placements.</summary>
    public string MetaCreativeHint { get; set; } = string.Empty;

    /// <summary>Prompt passed to the image model (after LLM refinement).</summary>
    public string ImagePrompt { get; set; } = string.Empty;
}

public class UploadMetaImageResponse
{
    public string ImageHash { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;

    /// <summary>Same hash as <see cref="ImageHash"/> for clients expecting snake_case.</summary>
    [JsonPropertyName("image_hash")]
    public string ImageHashSnake => ImageHash;
}

public class CreateMetaCampaignRequest
{
    // --- Local (NextHire) metadata — not sent to Meta ---

    [Required]
    public string TenantId { get; set; } = string.Empty;

    [Required]
    public Guid JobId { get; set; }

    /// <summary>
    /// <c>whatsapp</c> = link ad to a <c>wa.me</c> URL (no Page↔WABA link required; legacy behavior).
    /// <c>whatsapp_native</c> = Meta Click-to-WhatsApp (requires Page linked to WhatsApp Business).
    /// <c>job_post_url</c> = web traffic to a public job URL.
    /// <c>calendly</c> = web traffic to the org Calendly scheduling URL.
    /// </summary>
    [RegularExpression("^(whatsapp|whatsapp_native|job_post_url|calendly)$")]
    public string? DestinationType { get; set; }

    public string? WhatsappMessage { get; set; }

    /// <summary>
    /// Creative image as raw base64 (no data-URL prefix). Stored locally only — not sent to Meta Graph.
    /// </summary>
    public string? ImageBase64 { get; set; }

    /// <summary>MIME type for <see cref="ImageBase64"/>, e.g. image/png.</summary>
    public string? ImageContentType { get; set; }

    /// <summary>Ignored — local row is always saved after Meta succeeds. Kept for API compatibility.</summary>
    public bool PersistLocalRecord { get; set; } = true;

    // --- Full Graph API control (see Meta Marketing API). ExtensionData on each type accepts any extra field. ---

    [Required]
    public MetaCampaignGraphPayload? Campaign { get; set; }

    [Required]
    public MetaAdSetGraphPayload? AdSet { get; set; }

    [Required]
    public MetaAdCreativeGraphPayload? Creative { get; set; }

    [Required]
    public MetaAdGraphPayload? Ad { get; set; }
}

public class CreateMetaCampaignResponse
{
    /// <summary>Local <c>marketing_meta_campaigns</c> row id.</summary>
    public Guid? LocalRecordId { get; set; }

    public bool PersistedLocally { get; set; }

    public string CampaignId { get; set; } = string.Empty;
    public string AdSetId { get; set; } = string.Empty;
    public string CreativeId { get; set; } = string.Empty;
    public string AdId { get; set; } = string.Empty;
    public string AdAccountId { get; set; } = string.Empty;
    public string AdsManagerUrl { get; set; } = string.Empty;
}

public class MetaMarketingCampaignActionResponse
{
    public Guid? LocalRecordId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? MetaCampaignId { get; set; }
    public string? MetaAdSetId { get; set; }
    public string? MetaAdId { get; set; }
}

/// <summary>Publish an organic Facebook Page post from a Meta campaign creative.</summary>
public class PublishFacebookPagePostRequest
{
    /// <summary>Post caption / description shown on the Page.</summary>
    [Required]
    [MaxLength(5000)]
    public string Message { get; set; } = string.Empty;

    /// <summary>Destination URL included in the post (usually the campaign landing link).</summary>
    [Required]
    [MaxLength(2000)]
    public string Link { get; set; } = string.Empty;

    /// <summary>Raw base64 creative image (no data-URL prefix). Optional if already stored on the campaign.</summary>
    public string? ImageBase64 { get; set; }

    public string? ImageContentType { get; set; }
}

public class PublishFacebookPagePostResponse
{
    public string PostId { get; set; } = string.Empty;
    public string PageId { get; set; } = string.Empty;
    public string? PermalinkUrl { get; set; }
    public bool PermissionError { get; set; }
}

/// <summary>Row from <c>dbo.marketing_meta_campaigns</c> (not <c>sourcing_campaigns</c>).</summary>
public class MetaMarketingCampaignDto
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public string CampaignName { get; set; } = string.Empty;
    public string DestinationType { get; set; } = string.Empty;
    public string DestinationUrl { get; set; } = string.Empty;
    public string? WhatsappMessage { get; set; }
    public string AdText { get; set; } = string.Empty;
    public string ImageHash { get; set; } = string.Empty;
    public string? ImageBase64 { get; set; }
    public string? ImageContentType { get; set; }
    public string? MetaCampaignId { get; set; }
    public string? MetaAdSetId { get; set; }
    public string? MetaCreativeId { get; set; }
    public string? MetaAdId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public class ResolveMetaGeoRequest
{
    /// <summary>
    /// Human label from the UI (can come from Google Places), e.g. "Miami, FL, USA".
    /// </summary>
    [Required]
    [MaxLength(300)]
    public string Query { get; set; } = string.Empty;

    /// <summary>Optional ISO country code to constrain search, e.g. "US".</summary>
    [MaxLength(5)]
    public string? CountryCode { get; set; }

    /// <summary>Optional location types to constrain results: city, region, country, zip.</summary>
    public string[]? LocationTypes { get; set; }

    /// <summary>Max results to return (default 10).</summary>
    [Range(1, 50)]
    public int? Limit { get; set; }
}

public class ResolveMetaGeoResponse
{
    public IReadOnlyList<MetaGeoCandidate> Candidates { get; set; } = Array.Empty<MetaGeoCandidate>();

    /// <summary>Best guess candidate (usually first result).</summary>
    public MetaGeoCandidate? Best { get; set; }
}

public class MetaGeoCandidate
{
    /// <summary>Meta geo key used in targeting payloads.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Display name from Meta.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Meta location type, e.g. city/region/country/zip.</summary>
    public string? Type { get; set; }

    public string? CountryCode { get; set; }
    public string? CountryName { get; set; }
    public string? Region { get; set; }
}

/// <summary>Normalized Meta Ads Insights for one campaign (live from Graph API).</summary>
public class MetaCampaignInsightsDto
{
    public Guid? LocalRecordId { get; set; }
    public string? MetaCampaignId { get; set; }
    public string? CampaignName { get; set; }
    public string DatePreset { get; set; } = "maximum";
    public string? DateStart { get; set; }
    public string? DateStop { get; set; }

    public long? Impressions { get; set; }
    public long? Reach { get; set; }
    public long? Clicks { get; set; }
    public long? UniqueClicks { get; set; }
    public long? InlineLinkClicks { get; set; }
    public long? OutboundClicks { get; set; }

    public decimal? Spend { get; set; }
    public decimal? Cpc { get; set; }
    public decimal? Cpm { get; set; }
    public decimal? Cpp { get; set; }
    public decimal? Ctr { get; set; }
    public decimal? Frequency { get; set; }
    public decimal? CostPerInlineLinkClick { get; set; }

    /// <summary>Lead / conversion count derived from Meta <c>actions</c> (lead, onsite_conversion.lead_grouped, etc.).</summary>
    public long? MetaLeads { get; set; }
    public decimal? CostPerLead { get; set; }

    public IReadOnlyList<MetaInsightActionDto> Actions { get; set; } = Array.Empty<MetaInsightActionDto>();
    public IReadOnlyList<MetaInsightActionDto> CostPerActionType { get; set; } = Array.Empty<MetaInsightActionDto>();

    /// <summary>True when Meta returned no insights rows (common for brand-new campaigns).</summary>
    public bool Empty { get; set; }
}

public class MetaInsightActionDto
{
    public string ActionType { get; set; } = string.Empty;
    public decimal Value { get; set; }
}

public class MetaCampaignInsightsListResponse
{
    public string DatePreset { get; set; } = "maximum";
    public IReadOnlyList<MetaCampaignInsightsDto> Items { get; set; } = Array.Empty<MetaCampaignInsightsDto>();
}

/// <summary>One campaign's metrics for a captured week (historical snapshot).</summary>
public class MetaInsightsSnapshotMetricsDto
{
    public decimal? Spend { get; set; }
    public long? Impressions { get; set; }
    public long? Reach { get; set; }
    public long? Clicks { get; set; }
    public long? InlineLinkClicks { get; set; }
    public decimal? Cpc { get; set; }
    public decimal? Cpm { get; set; }
    public decimal? Ctr { get; set; }
    public long? MetaLeads { get; set; }
    public decimal? CostPerLead { get; set; }
    public int NexthireLeadsCount { get; set; }
    public decimal? CostPerCandidate { get; set; }
    public string? DatePreset { get; set; }
    public string? CapturedAtUtc { get; set; }
    public string? Source { get; set; }
}

public class MetaInsightsHistoryCampaignCompareDto
{
    public string MetaCampaignId { get; set; } = string.Empty;
    public string CampaignName { get; set; } = string.Empty;
    public string Platform { get; set; } = "meta_ads";
    public Guid? LocalCampaignId { get; set; }
    public MetaInsightsSnapshotMetricsDto? WeekA { get; set; }
    public MetaInsightsSnapshotMetricsDto? WeekB { get; set; }
    public MetaInsightsSnapshotMetricsDto? Delta { get; set; }
}

public class MetaInsightsHistoryResponse
{
    public IReadOnlyList<string> Weeks { get; set; } = Array.Empty<string>();
    public string? WeekA { get; set; }
    public string? WeekB { get; set; }
    public IReadOnlyList<MetaInsightsHistoryCampaignCompareDto> Campaigns { get; set; } =
        Array.Empty<MetaInsightsHistoryCampaignCompareDto>();
    public MetaInsightsSnapshotMetricsDto? TotalsWeekA { get; set; }
    public MetaInsightsSnapshotMetricsDto? TotalsWeekB { get; set; }
    public MetaInsightsSnapshotMetricsDto? TotalsDelta { get; set; }
}

/// <summary>Live Meta Ad Account health (payment / disable status).</summary>
public class MetaAdAccountStatusDto
{
    public string AdAccountId { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string Currency { get; set; } = "USD";

    /// <summary>Raw Meta <c>account_status</c> code.</summary>
    public int AccountStatus { get; set; }

    /// <summary>Normalized status: active, disabled, unsettled, pending, grace, closed, unknown.</summary>
    public string StatusKey { get; set; } = "unknown";

    public string StatusLabel { get; set; } = string.Empty;

    /// <summary>True when ads can typically deliver (ACTIVE / IN_GRACE_PERIOD).</summary>
    public bool IsHealthy { get; set; }

    /// <summary>True for payment / billing related disable or unsettled states.</summary>
    public bool IsPaymentIssue { get; set; }

    public int? DisableReason { get; set; }
    public string? DisableReasonLabel { get; set; }

    public decimal? AmountSpent { get; set; }
    public decimal? Balance { get; set; }
    public decimal? SpendCap { get; set; }

    public string? FundingSourceDisplay { get; set; }
}
