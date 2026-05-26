using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using nexthire_api.DTOs;
using nexthire_api.DTOs.Meta;
using nexthire_api.Options;
using nexthire_api.Repositories;
using nexthire_api.Repositories.Sourcing;
using OpenAI.Chat;

namespace nexthire_api.Services;

public class MetaAdsService : IMetaAdsService
{
    private const string HttpClientName = "MetaGraph";
    private const string AzureOpenAiHttpClient = "AzureOpenAI";
    private const string MetaSourceTypeCode = "meta_ads";
    private const string DefaultGraphApiVersion = "v23.0";

    private const string DallePromptSystemMessage = """
You write one English prompt for an image model to create a recruiting ad for Meta (Facebook/Instagram).

Output rules:
- Output ONLY the image prompt text. No title, no quotes, no markdown, no bullet list.
- Reflect the job's industry, seniority, and tone from the title and description.
- Design as a square ad (1:1), professional, eye-catching, with clean hierarchy like social recruiting creatives.
- MUST include clear, readable overlay text in the final image with this exact headline format:
  "WE ARE HIRING: <JOB_TITLE>"
- Add one short supporting line (e.g., "Apply now" / "Join our team"). Keep text minimal and readable.
- No logos, no watermarks, no phone/UI screenshots, no brand names, no real person's likeness.
- Brand-safe and suitable for paid employment advertising.
""";

    private static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg"
    };

    private static readonly HashSet<string> AllowedPublisherPlatforms = new(StringComparer.OrdinalIgnoreCase)
    {
        "facebook", "instagram", "audience_network", "messenger"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ISourcingRepository _sourcingRepository;
    private readonly IJobRepository _jobRepository;
    private readonly IMarketingMetaCampaignRepository _marketingRepo;
    private readonly ILogger<MetaAdsService> _logger;

    private readonly JsonSerializerOptions _jsonSnake = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly JsonSerializerOptions _jsonConfig = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record ResolvedMetaAdsContext(
        string AccessToken,
        string AdAccountId,
        string PageId,
        string? WhatsappPhoneNumber,
        string ApiVersion);

    public MetaAdsService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ISourcingRepository sourcingRepository,
        IJobRepository jobRepository,
        IMarketingMetaCampaignRepository marketingRepo,
        ILogger<MetaAdsService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _sourcingRepository = sourcingRepository;
        _jobRepository = jobRepository;
        _marketingRepo = marketingRepo;
        _logger = logger;
    }

    public async Task<UploadMetaImageResponse> UploadCreativeImageAsync(
        Guid orgId,
        IFormFile file,
        CancellationToken cancellationToken = default)
    {
        if (file == null || file.Length == 0)
            throw new ArgumentException("file is required.");

        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrEmpty(ext) || !AllowedImageExtensions.Contains(ext))
            throw new ArgumentException("Only png, jpg, and jpeg images are allowed.");

        var contentType = file.ContentType?.ToLowerInvariant() ?? "";
        if (!contentType.Contains("png", StringComparison.Ordinal) &&
            !contentType.Contains("jpeg", StringComparison.Ordinal) &&
            !contentType.Contains("jpg", StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid image content type.");
        }

        await using var stream = file.OpenReadStream();
        var mime = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
        return await UploadAdImageToMetaAsync(
            orgId,
            stream,
            file.FileName,
            mime,
            file.Length,
            cancellationToken);
    }

    public async Task<GenerateMetaCreativePreviewResponse> GenerateCreativePreviewFromJobAsync(
        Guid orgId,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var job = await _jobRepository.GetByIdAsync(orgId, jobId);
        if (job == null)
            throw new ArgumentException("jobId was not found for this organization.");

        var title = string.IsNullOrWhiteSpace(job.Title) ? "Open role" : job.Title.Trim();
        var description = string.IsNullOrWhiteSpace(job.Description)
            ? "General professional opportunity."
            : job.Description.Trim();
        if (description.Length > 8000)
            description = description[..8000] + "…";

        var imagePrompt = await BuildDallePromptFromJobAsync(title, description, cancellationToken);
        if (imagePrompt.Length > 3500)
            imagePrompt = imagePrompt[..3500];

        var size = _configuration["AzureOpenAI:CreativeImageSize"]?.Trim();
        if (string.IsNullOrEmpty(size))
            size = "1024x1024";

        var (b64, revised) = await CallAzureImageGenerationsAsync(imagePrompt, size, cancellationToken);
        var (w, h) = ParseSize(size);

        _logger.LogInformation(
            "Generated Meta creative preview for org {OrgId} job {JobId} size {Size}",
            orgId,
            jobId,
            size);

        var hint =
            "Meta commonly uses 1080×1080 (1:1) for feed placements; this image is " + size +
            " from the image model — acceptable for many placements. For exact specs, export at 1080×1080 in your design tool if required.";

        return new GenerateMetaCreativePreviewResponse
        {
            ImageBase64 = b64,
            ContentType = "image/png",
            Width = w,
            Height = h,
            MetaCreativeHint = hint,
            ImagePrompt = string.IsNullOrWhiteSpace(revised) ? imagePrompt : revised.Trim()
        };
    }

    private async Task<UploadMetaImageResponse> UploadAdImageToMetaAsync(
        Guid orgId,
        Stream stream,
        string fileName,
        string contentType,
        long length,
        CancellationToken cancellationToken)
    {
        var meta = await ResolveMetaAdsContextAsync(orgId, cancellationToken);

        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var fileContent = new StreamContent(stream);
        var mime = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(mime);

        using var form = new MultipartFormDataContent();
        form.Add(fileContent, "filename", fileName);

        var url = BuildGraphUrl(meta.ApiVersion, $"{meta.AdAccountId}/adimages", meta.AccessToken);
        _logger.LogInformation(
            "Uploading Meta ad image for org {OrgId}. AdAccount={AdAccount}, FileName={FileName}, Length={Length}",
            orgId,
            meta.AdAccountId,
            fileName,
            length);

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        using var response = await client.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw await ParseMetaErrorAsync(response, payload, cancellationToken);

        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Object)
            throw new MetaGraphApiException("Unexpected adimages response shape.", (int)response.StatusCode);

        foreach (var prop in images.EnumerateObject())
        {
            var img = prop.Value;
            var hash = img.TryGetProperty("hash", out var h) ? h.GetString() : null;
            var urlProp = img.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (!string.IsNullOrEmpty(hash))
            {
                return new UploadMetaImageResponse
                {
                    ImageHash = hash!,
                    ImageUrl = urlProp ?? string.Empty,
                    FileName = fileName
                };
            }
        }

        throw new MetaGraphApiException("Meta did not return an image hash.", (int)response.StatusCode);
    }

    public async Task<CreateMetaCampaignResponse> CreateCampaignAsync(
        Guid orgId,
        CreateMetaCampaignRequest request,
        CancellationToken cancellationToken = default)
    {
        var meta = await ResolveMetaAdsContextAsync(orgId, cancellationToken);
        ArgumentNullException.ThrowIfNull(request);

        if (!TenantMatchesOrg(request.TenantId, orgId))
            throw new ArgumentException("tenantId does not match the authenticated organization.");

        var job = await _jobRepository.GetByIdAsync(orgId, request.JobId);
        if (job == null)
            throw new ArgumentException("jobId was not found for this organization.");

        var adAccountId = meta.AdAccountId;
        var version = meta.ApiVersion;
        var client = _httpClientFactory.CreateClient(HttpClientName);

        var campaignBody = MetaGraphPayloadBuilder.ToGraphObject(request.Campaign);
        var adSetBody = MetaGraphPayloadBuilder.ToGraphObject(request.AdSet);
        var creativeBody = MetaGraphPayloadBuilder.ToGraphObject(request.Creative);
        var adBody = MetaGraphPayloadBuilder.ToGraphObject(request.Ad);

        ValidateGraphPayloads(campaignBody, adSetBody, creativeBody, adBody);

        // Apply safe defaults if FE didn't set statuses.
        if (!campaignBody.ContainsKey("status")) campaignBody["status"] = "PAUSED";
        if (!adSetBody.ContainsKey("status")) adSetBody["status"] = "PAUSED";
        if (!adBody.ContainsKey("status")) adBody["status"] = "PAUSED";

        ApplyCampaignGraphDefaults(campaignBody);
        ApplySpecialAdCategoryCountry(campaignBody, adSetBody);
        ApplySpecialAdCategoryAudienceRestrictions(campaignBody, adSetBody);
        SanitizeAdSetGeoTargeting(adSetBody);
        ValidateAdSetGeoTargeting(adSetBody);
        ValidateCreativeDestinationUrl(creativeBody);

        // Ensure creative has the tenant's page_id.
        if (creativeBody["object_story_spec"] is JsonObject story)
        {
            if (!story.ContainsKey("page_id") && !string.IsNullOrWhiteSpace(meta.PageId))
                story["page_id"] = meta.PageId;
        }
        else
        {
            creativeBody["object_story_spec"] = new JsonObject { ["page_id"] = meta.PageId };
        }

        ApplyDestinationTypeConfiguration(
            request, campaignBody, adSetBody, creativeBody, meta.PageId, meta.WhatsappPhoneNumber);
        ValidateObjectiveCreativeAlignment(request, campaignBody, adSetBody, creativeBody);

        var imageHash = ResolveImageHashFromCreativeBody(creativeBody);
        if (!string.IsNullOrWhiteSpace(imageHash))
        {
            await VerifyImageHashExistsAsync(
                client,
                version,
                adAccountId,
                meta.AccessToken,
                imageHash,
                cancellationToken);
        }
        var campaignName = campaignBody["name"]?.GetValue<string>() ?? "Campaign";
        _logger.LogInformation(
            "Creating Meta campaign for org {OrgId}, job {JobId}, name {Name}",
            orgId,
            request.JobId,
            campaignName);

        var campaignId = await PostJsonForIdAsync(
            client,
            version,
            adAccountId,
            meta.AccessToken,
            "campaigns",
            campaignBody,
            cancellationToken);

        MetaGraphPayloadBuilder.SetRequired(adSetBody, "campaign_id", campaignId);
        var adSetId = await PostJsonForIdAsync(
            client,
            version,
            adAccountId,
            meta.AccessToken,
            "adsets",
            adSetBody,
            cancellationToken);

        var creativeId = await PostJsonForIdAsync(
            client,
            version,
            adAccountId,
            meta.AccessToken,
            "adcreatives",
            creativeBody,
            cancellationToken);

        if (!adBody.ContainsKey("creative"))
            adBody["creative"] = new JsonObject { ["creative_id"] = creativeId };
        else if (adBody["creative"] is JsonObject creativeRef &&
                 string.IsNullOrWhiteSpace(creativeRef["creative_id"]?.GetValue<string>()))
        {
            creativeRef["creative_id"] = creativeId;
        }

        adBody["adset_id"] = adSetId;

        var adId = await PostJsonForIdAsync(
            client,
            version,
            adAccountId,
            meta.AccessToken,
            "ads",
            adBody,
            cancellationToken);

        var adsManagerUrl = BuildAdsManagerUrl(adAccountId);

        var row = BuildLocalMarketingRow(
            orgId,
            request,
            campaignBody,
            creativeBody,
            imageHash,
            campaignId,
            adSetId,
            creativeId,
            adId);

        try
        {
            await _marketingRepo.InsertAsync(row, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Meta campaign created remotely but local persist failed. MetaCampaignId={MetaCampaignId}, LocalId={LocalId}",
                campaignId,
                row.Id);

            throw new InvalidOperationException(
                $"Campaign was created in Meta (campaignId={campaignId}) but could not be saved to marketing_meta_campaigns: {ex.Message}",
                ex);
        }

        var saved = await _marketingRepo.GetByIdAsync(row.Id, cancellationToken);
        if (saved == null)
        {
            throw new InvalidOperationException(
                $"Campaign was created in Meta (campaignId={campaignId}) but the row could not be read back from marketing_meta_campaigns (id={row.Id}). " +
                "Check that the API ConnectionStrings:NextHireDb points to the same database you are querying.");
        }

        var metaStatus = campaignBody["status"]?.GetValue<string>() ?? "PAUSED";
        var sourcingStatus = MapMetaStatusToSourcingStatus(metaStatus);
        var dailyBudget = TryGetDailyBudgetDollars(adSetBody);
        var landingUrl = TryGetNestedString(creativeBody, "object_story_spec", "link_data", "link") ?? string.Empty;

        try
        {
            await _sourcingRepository.UpsertMetaAdsCampaignAsync(
                orgId,
                row.Id,
                request.JobId,
                campaignName,
                sourcingStatus,
                dailyBudget,
                string.IsNullOrWhiteSpace(landingUrl) ? null : landingUrl,
                campaignId,
                adAccountId,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Meta campaign saved to marketing_meta_campaigns but sourcing_campaigns sync failed. LocalId={LocalId}",
                row.Id);
            throw new InvalidOperationException(
                $"Campaign was created in Meta but could not be synced to sourcing_campaigns: {ex.Message}",
                ex);
        }

        _logger.LogInformation(
            "Meta campaign created and stored. LocalId={LocalId}, MetaCampaignId={MetaCampaignId}",
            row.Id,
            campaignId);

        return new CreateMetaCampaignResponse
        {
            LocalRecordId = row.Id,
            PersistedLocally = true,
            CampaignId = campaignId,
            AdSetId = adSetId,
            CreativeId = creativeId,
            AdId = adId,
            AdAccountId = adAccountId,
            AdsManagerUrl = adsManagerUrl
        };
    }

    public async Task<IReadOnlyList<MetaMarketingCampaignDto>> ListMarketingCampaignsAsync(
        Guid orgId,
        Guid? jobId = null,
        CancellationToken cancellationToken = default)
    {
        var tenantId = orgId.ToString("D");
        var rows = await _marketingRepo.ListByTenantAsync(tenantId, jobId, cancellationToken);
        return rows.Select(MapMarketingCampaignDto).ToList();
    }

    public async Task<MetaMarketingCampaignDto?> GetMarketingCampaignAsync(
        Guid orgId,
        Guid localRecordId,
        CancellationToken cancellationToken = default)
    {
        var tenantId = orgId.ToString("D");
        var row = await _marketingRepo.GetByIdForTenantAsync(localRecordId, tenantId, cancellationToken)
                  ?? await _marketingRepo.GetByIdAsync(localRecordId, cancellationToken);

        if (row == null || !TenantMatchesOrg(row.TenantId, orgId))
            return null;

        return MapMarketingCampaignDto(row);
    }

    public async Task SyncMarketingCampaignsToSourcingAsync(
        Guid orgId,
        CancellationToken cancellationToken = default)
    {
        var tenantId = orgId.ToString("D");
        var rows = await _marketingRepo.ListByTenantAsync(tenantId, jobId: null, cancellationToken);
        if (rows.Count == 0)
            return;

        string adAccountId = string.Empty;
        try
        {
            var meta = await ResolveMetaAdsContextAsync(orgId, cancellationToken);
            adAccountId = meta.AdAccountId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Meta Ads connection unavailable; syncing marketing campaigns to sourcing without ad account id.");
        }

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.MetaCampaignId))
                continue;

            try
            {
                await _sourcingRepository.EnsureMetaAdsCampaignRowAsync(
                    orgId,
                    row.Id,
                    row.JobId,
                    row.CampaignName,
                    MapMetaStatusToSourcingStatus(row.Status),
                    dailyBudget: null,
                    string.IsNullOrWhiteSpace(row.DestinationUrl) ? null : row.DestinationUrl,
                    row.MetaCampaignId,
                    adAccountId,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to sync marketing campaign {LocalId} to sourcing_campaigns.",
                    row.Id);
            }
        }
    }

    private static MetaMarketingCampaignDto MapMarketingCampaignDto(MarketingMetaCampaignRow row) =>
        new()
        {
            Id = row.Id,
            JobId = row.JobId,
            CampaignName = row.CampaignName,
            DestinationType = row.DestinationType,
            DestinationUrl = row.DestinationUrl,
            WhatsappMessage = row.WhatsappMessage,
            AdText = row.AdText,
            ImageHash = row.ImageHash,
            MetaCampaignId = row.MetaCampaignId,
            MetaAdSetId = row.MetaAdsetId,
            MetaCreativeId = row.MetaCreativeId,
            MetaAdId = row.MetaAdId,
            Status = row.Status,
            CreatedAtUtc = row.CreatedAtUtc,
            UpdatedAtUtc = row.UpdatedAtUtc
        };

    public async Task<MetaMarketingCampaignActionResponse> PauseMarketingCampaignAsync(
        Guid orgId,
        string campaignRef,
        CancellationToken cancellationToken = default)
    {
        var target = await ResolveCampaignActionTargetAsync(orgId, campaignRef, cancellationToken);
        var meta = await ResolveMetaAdsContextAsync(orgId, cancellationToken);
        var client = _httpClientFactory.CreateClient(HttpClientName);

        if (!string.IsNullOrWhiteSpace(target.MetaAdId))
        {
            await UpdateMetaNodeStatusAsync(
                client, meta.ApiVersion, target.MetaAdId, meta.AccessToken, "PAUSED", cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(target.MetaAdSetId))
        {
            await UpdateMetaNodeStatusAsync(
                client, meta.ApiVersion, target.MetaAdSetId, meta.AccessToken, "PAUSED", cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(target.MetaCampaignId))
        {
            await UpdateMetaNodeStatusAsync(
                client, meta.ApiVersion, target.MetaCampaignId, meta.AccessToken, "PAUSED", cancellationToken);
        }

        if (target.LocalRecordId is Guid localId)
        {
            var tenantId = orgId.ToString("D");
            var updated = await _marketingRepo.UpdateStatusForTenantAsync(
                localId, tenantId, "PAUSED", cancellationToken);
            if (!updated)
                updated = await _marketingRepo.UpdateStatusByIdAsync(localId, "PAUSED", cancellationToken);
            if (!updated)
                throw new InvalidOperationException("Failed to update local marketing campaign status.");

            try
            {
                await _sourcingRepository.PatchCampaignStatusAsync(orgId, localId, "paused");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Paused Meta campaign but failed to update sourcing_campaigns status for {LocalId}.",
                    localId);
            }
        }

        _logger.LogInformation(
            "Paused Meta marketing campaign. LocalId={LocalId}, MetaCampaignId={MetaCampaignId}",
            target.LocalRecordId,
            target.MetaCampaignId);

        return new MetaMarketingCampaignActionResponse
        {
            LocalRecordId = target.LocalRecordId,
            Status = "PAUSED",
            MetaCampaignId = target.MetaCampaignId,
            MetaAdSetId = target.MetaAdSetId,
            MetaAdId = target.MetaAdId
        };
    }

    public async Task<MetaMarketingCampaignActionResponse> ActivateMarketingCampaignAsync(
        Guid orgId,
        string campaignRef,
        CancellationToken cancellationToken = default)
    {
        var target = await ResolveCampaignActionTargetAsync(orgId, campaignRef, cancellationToken);
        var meta = await ResolveMetaAdsContextAsync(orgId, cancellationToken);
        var client = _httpClientFactory.CreateClient(HttpClientName);

        if (!string.IsNullOrWhiteSpace(target.MetaAdId))
        {
            await UpdateMetaNodeStatusAsync(
                client, meta.ApiVersion, target.MetaAdId, meta.AccessToken, "ACTIVE", cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(target.MetaAdSetId))
        {
            await UpdateMetaNodeStatusAsync(
                client, meta.ApiVersion, target.MetaAdSetId, meta.AccessToken, "ACTIVE", cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(target.MetaCampaignId))
        {
            await UpdateMetaNodeStatusAsync(
                client, meta.ApiVersion, target.MetaCampaignId, meta.AccessToken, "ACTIVE", cancellationToken);
        }

        if (target.LocalRecordId is Guid localId)
        {
            var tenantId = orgId.ToString("D");
            var updated = await _marketingRepo.UpdateStatusForTenantAsync(
                localId, tenantId, "ACTIVE", cancellationToken);
            if (!updated)
                updated = await _marketingRepo.UpdateStatusByIdAsync(localId, "ACTIVE", cancellationToken);
            if (!updated)
                throw new InvalidOperationException("Failed to update local marketing campaign status.");

            try
            {
                await _sourcingRepository.PatchCampaignStatusAsync(orgId, localId, "active");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Activated Meta campaign but failed to update sourcing_campaigns status for {LocalId}.",
                    localId);
            }
        }

        _logger.LogInformation(
            "Activated Meta marketing campaign. LocalId={LocalId}, MetaCampaignId={MetaCampaignId}",
            target.LocalRecordId,
            target.MetaCampaignId);

        return new MetaMarketingCampaignActionResponse
        {
            LocalRecordId = target.LocalRecordId,
            Status = "ACTIVE",
            MetaCampaignId = target.MetaCampaignId,
            MetaAdSetId = target.MetaAdSetId,
            MetaAdId = target.MetaAdId
        };
    }

    /// <summary>
    /// Meta requires <c>is_adset_budget_sharing_enabled</c> when budget lives on the ad set, not the campaign.
    /// </summary>
    private static void ApplyCampaignGraphDefaults(JsonObject campaignBody)
    {
        var hasCampaignLevelBudget =
            campaignBody.ContainsKey("daily_budget") ||
            campaignBody.ContainsKey("lifetime_budget");

        if (!hasCampaignLevelBudget &&
            !campaignBody.ContainsKey("is_adset_budget_sharing_enabled"))
        {
            campaignBody["is_adset_budget_sharing_enabled"] = false;
        }

        if (!campaignBody.ContainsKey("special_ad_categories"))
            campaignBody["special_ad_categories"] = new JsonArray();
    }

    private const int SpecialAdCategoryRequiredAgeMin = 18;
    private const int SpecialAdCategoryRequiredAgeMax = 65;

    private static readonly HashSet<string> SpecialAdCategoriesRequiringCountry =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "EMPLOYMENT",
            "HOUSING",
            "FINANCIAL_PRODUCTS_SERVICES",
            "CREDIT",
            "ISSUES_ELECTIONS_POLITICS"
        };

    private static readonly HashSet<string> SpecialAdCategoriesWithStandardAudienceRestrictions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "EMPLOYMENT",
            "HOUSING",
            "FINANCIAL_PRODUCTS_SERVICES",
            "CREDIT"
        };

    /// <summary>
    /// Meta requires <c>special_ad_category_country</c> when <c>special_ad_categories</c> is set.
    /// If omitted, Meta defaults to the ad account tax country, which often mismatches US city targeting.
    /// </summary>
    private static void ApplySpecialAdCategoryCountry(JsonObject campaignBody, JsonObject adSetBody)
    {
        if (!TryGetSpecialAdCategories(campaignBody, out var categories) || categories.Count == 0)
            return;

        if (HasNonEmptyStringArray(campaignBody, "special_ad_category_country"))
            return;

        var geoCountries = TryGetGeoCountryCodes(adSetBody);
        if (geoCountries.Count > 0)
        {
            campaignBody["special_ad_category_country"] = ToJsonStringArray(geoCountries);
            return;
        }

        if (!categories.Any(c => SpecialAdCategoriesRequiringCountry.Contains(c)))
            return;

        throw new ArgumentException(
            "campaign.specialAdCategoryCountry is required when campaign.specialAdCategories includes " +
            "EMPLOYMENT, HOUSING, FINANCIAL_PRODUCTS_SERVICES, or ISSUES_ELECTIONS_POLITICS. " +
            "Use ISO country codes that match your ad set audience (e.g. [\"US\"] for a US city).");
    }

    /// <summary>
    /// Housing, employment, and financial special ad categories only allow age 18–65+ (#2909037).
    /// </summary>
    private static void ApplySpecialAdCategoryAudienceRestrictions(JsonObject campaignBody, JsonObject adSetBody)
    {
        if (!TryGetSpecialAdCategories(campaignBody, out var categories) || categories.Count == 0)
            return;

        if (!categories.Any(c => SpecialAdCategoriesWithStandardAudienceRestrictions.Contains(c)))
            return;

        if (adSetBody["targeting"] is not JsonObject targeting)
            return;

        targeting["age_min"] = SpecialAdCategoryRequiredAgeMin;
        targeting["age_max"] = SpecialAdCategoryRequiredAgeMax;
    }

    private const string ClickToWhatsAppCreativeLink = "https://api.whatsapp.com/send";

    /// <summary>
    /// Aligns ad set + creative with Meta Click-to-WhatsApp or web traffic requirements based on <see cref="CreateMetaCampaignRequest.DestinationType"/>.
    /// </summary>
    private static void ApplyDestinationTypeConfiguration(
        CreateMetaCampaignRequest request,
        JsonObject campaignBody,
        JsonObject adSetBody,
        JsonObject creativeBody,
        string pageId,
        string? whatsappPhoneNumber)
    {
        var destination = request.DestinationType?.Trim().ToLowerInvariant();
        if (destination == "whatsapp")
        {
            ApplyWhatsAppLinkConfiguration(
                request, campaignBody, adSetBody, creativeBody, whatsappPhoneNumber);
            return;
        }

        if (destination == "whatsapp_native")
        {
            ApplyClickToWhatsAppConfiguration(adSetBody, creativeBody, pageId);
            return;
        }

        if (destination == "job_post_url")
            ApplyWebJobPostConfiguration(adSetBody, creativeBody);
    }

    /// <summary>
    /// Link-style WhatsApp ads: traffic objective + <c>wa.me</c> URL. Does not require Page↔WABA integration.
    /// </summary>
    private static void ApplyWhatsAppLinkConfiguration(
        CreateMetaCampaignRequest request,
        JsonObject campaignBody,
        JsonObject adSetBody,
        JsonObject creativeBody,
        string? whatsappPhoneNumber)
    {
        adSetBody.Remove("destination_type");
        adSetBody.Remove("promoted_object");

        var objective = campaignBody["objective"]?.GetValue<string>();
        var optimization = adSetBody["optimization_goal"]?.GetValue<string>();

        if (string.Equals(objective, "OUTCOME_ENGAGEMENT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(optimization, "CONVERSATIONS", StringComparison.OrdinalIgnoreCase))
        {
            campaignBody["objective"] = "OUTCOME_TRAFFIC";
            adSetBody["optimization_goal"] = "LINK_CLICKS";
        }

        var linkData = GetOrCreateLinkData(creativeBody);

        if (string.Equals(
                TryGetNestedString(linkData, "call_to_action", "type"),
                "WHATSAPP_MESSAGE",
                StringComparison.OrdinalIgnoreCase))
        {
            linkData["call_to_action"] = new JsonObject { ["type"] = "LEARN_MORE" };
        }

        var link = ResolveWhatsAppDestinationUrl(linkData, request.WhatsappMessage, whatsappPhoneNumber);
        if (!string.IsNullOrWhiteSpace(link))
            linkData["link"] = link;
    }

    private static JsonObject GetOrCreateLinkData(JsonObject creativeBody)
    {
        if (creativeBody["object_story_spec"] is not JsonObject story)
        {
            story = new JsonObject();
            creativeBody["object_story_spec"] = story;
        }

        if (story["link_data"] is not JsonObject linkData)
        {
            linkData = new JsonObject();
            story["link_data"] = linkData;
        }

        return linkData;
    }

    private static string? ResolveWhatsAppDestinationUrl(
        JsonObject linkData,
        string? whatsappMessage,
        string? whatsappPhoneNumber)
    {
        var candidates = new[]
        {
            linkData["link"]?.GetValue<string>(),
            TryGetNestedString(linkData, "call_to_action", "value", "link"),
            whatsappMessage
        };

        foreach (var candidate in candidates)
        {
            if (IsUsableWhatsAppAdLink(candidate))
                return candidate!.Trim();
        }

        return BuildWaMeUrl(whatsappPhoneNumber, whatsappMessage);
    }

    private static bool IsUsableWhatsAppAdLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (string.Equals(url.Trim(), ClickToWhatsAppCreativeLink, StringComparison.OrdinalIgnoreCase))
            return false;

        return url.Contains("wa.me/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("whatsapp.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string? BuildWaMeUrl(string? phoneNumber, string? message)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
            return null;

        var digits = new string(phoneNumber.Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
            return null;

        var url = $"https://wa.me/{digits}";
        if (!string.IsNullOrWhiteSpace(message) &&
            !message.TrimStart().StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            url += $"?text={Uri.EscapeDataString(message.Trim())}";
        }

        return url;
    }

    private static void ApplyClickToWhatsAppConfiguration(
        JsonObject adSetBody,
        JsonObject creativeBody,
        string pageId)
    {
        adSetBody["destination_type"] = "WHATSAPP";

        if (!string.Equals(
                adSetBody["optimization_goal"]?.GetValue<string>(),
                "CONVERSATIONS",
                StringComparison.OrdinalIgnoreCase))
        {
            adSetBody["optimization_goal"] = "CONVERSATIONS";
        }

        var promoted = adSetBody["promoted_object"] as JsonObject ?? new JsonObject();
        if (!promoted.ContainsKey("page_id") && !string.IsNullOrWhiteSpace(pageId))
            promoted["page_id"] = pageId;
        adSetBody["promoted_object"] = promoted;

        if (creativeBody["object_story_spec"] is not JsonObject story ||
            story["link_data"] is not JsonObject linkData)
        {
            return;
        }

        linkData["link"] = ClickToWhatsAppCreativeLink;
        linkData["call_to_action"] = new JsonObject
        {
            ["type"] = "WHATSAPP_MESSAGE",
            ["value"] = new JsonObject { ["app_destination"] = "WHATSAPP" }
        };
    }

    private static void ApplyWebJobPostConfiguration(JsonObject adSetBody, JsonObject creativeBody)
    {
        if (adSetBody.TryGetPropertyValue("destination_type", out var destinationNode) &&
            string.Equals(destinationNode?.GetValue<string>(), "WHATSAPP", StringComparison.OrdinalIgnoreCase))
        {
            adSetBody.Remove("destination_type");
        }

        var optimization = adSetBody["optimization_goal"]?.GetValue<string>();
        if (string.Equals(optimization, "CONVERSATIONS", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(optimization, "LANDING_PAGE_VIEWS", StringComparison.OrdinalIgnoreCase))
        {
            adSetBody["optimization_goal"] = "LINK_CLICKS";
        }

        if (creativeBody["object_story_spec"] is not JsonObject story ||
            story["link_data"] is not JsonObject linkData)
        {
            return;
        }

        var ctaType = TryGetNestedString(linkData, "call_to_action", "type");
        if (string.Equals(ctaType, "WHATSAPP_MESSAGE", StringComparison.OrdinalIgnoreCase))
        {
            linkData["call_to_action"] = new JsonObject { ["type"] = "LEARN_MORE" };
        }
    }

    private static void ValidateObjectiveCreativeAlignment(
        CreateMetaCampaignRequest request,
        JsonObject campaignBody,
        JsonObject adSetBody,
        JsonObject creativeBody)
    {
        var destination = request.DestinationType?.Trim().ToLowerInvariant();
        var objective = campaignBody["objective"]?.GetValue<string>() ?? string.Empty;
        var optimization = adSetBody["optimization_goal"]?.GetValue<string>() ?? string.Empty;
        var adSetDestination = adSetBody["destination_type"]?.GetValue<string>();
        var ctaType = TryGetNestedString(creativeBody, "object_story_spec", "link_data", "call_to_action", "type");
        var link = TryGetNestedString(creativeBody, "object_story_spec", "link_data", "link");

        if (destination == "whatsapp")
        {
            if (string.Equals(adSetDestination, "WHATSAPP", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "destinationType whatsapp uses a wa.me link ad, not adSet.destinationType WHATSAPP. " +
                    "Use destinationType whatsapp_native for Click-to-WhatsApp.");
            }

            if (string.Equals(ctaType, "WHATSAPP_MESSAGE", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "destinationType whatsapp must not use WHATSAPP_MESSAGE CTA. Use LEARN_MORE with a wa.me link, " +
                    "or destinationType whatsapp_native if the Page is linked to WhatsApp Business.");
            }

            if (string.IsNullOrWhiteSpace(link))
            {
                throw new ArgumentException(
                    "destinationType whatsapp requires creative.objectStorySpec.linkData.link (e.g. https://wa.me/...), " +
                    "or whatsappMessage plus WhatsappPhoneNumber in the tenant meta_ads config_json.");
            }

            if (!link.Contains("wa.me/", StringComparison.OrdinalIgnoreCase) &&
                !link.Contains("whatsapp.com", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "destinationType whatsapp requires a WhatsApp URL (wa.me or whatsapp.com) in linkData.link.");
            }

            return;
        }

        if (destination == "whatsapp_native")
        {
            if (!IsWhatsAppCampaignObjective(objective))
            {
                throw new ArgumentException(
                    "whatsapp_native requires campaign.objective OUTCOME_ENGAGEMENT, OUTCOME_SALES, OUTCOME_TRAFFIC, or OUTCOME_LEADS.");
            }

            if (!string.Equals(adSetDestination, "WHATSAPP", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "whatsapp_native requires adSet.destinationType WHATSAPP (set automatically).");
            }

            if (!string.Equals(optimization, "CONVERSATIONS", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "whatsapp_native requires adSet.optimizationGoal CONVERSATIONS.");
            }

            if (!string.Equals(ctaType, "WHATSAPP_MESSAGE", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "whatsapp_native requires creative.objectStorySpec.linkData.callToAction.type WHATSAPP_MESSAGE.");
            }

            return;
        }

        if (destination == "job_post_url")
        {
            if (string.Equals(adSetDestination, "WHATSAPP", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Web job ads cannot use adSet.destinationType WHATSAPP. Set destinationType to job_post_url.");
            }

            if (string.Equals(objective, "OUTCOME_ENGAGEMENT", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(optimization, "CONVERSATIONS", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Web job ads should use campaign.objective OUTCOME_TRAFFIC and adSet.optimizationGoal LINK_CLICKS, not OUTCOME_ENGAGEMENT + CONVERSATIONS.");
            }

            if (string.Equals(ctaType, "WHATSAPP_MESSAGE", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Web job ads cannot use callToAction.type WHATSAPP_MESSAGE. Use LEARN_MORE or APPLY_NOW with a public job URL.");
            }

            if (!string.IsNullOrWhiteSpace(link) &&
                (link.Contains("wa.me/", StringComparison.OrdinalIgnoreCase) ||
                 link.Contains("api.whatsapp.com", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException(
                    "Web job ads require a public https job URL in creative.objectStorySpec.linkData.link, not a WhatsApp link.");
            }

            return;
        }

        // No destinationType hint — catch common mismatches early.
        if (string.Equals(objective, "OUTCOME_ENGAGEMENT", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(optimization, "CONVERSATIONS", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(adSetDestination, "WHATSAPP", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(ctaType, "LEARN_MORE", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Creative and objective mismatch: use destinationType whatsapp with OUTCOME_TRAFFIC + LINK_CLICKS + wa.me + LEARN_MORE, " +
                "or destinationType whatsapp_native with CONVERSATIONS + WHATSAPP_MESSAGE (Page must be linked to WhatsApp Business).");
        }
    }

    private static bool IsWhatsAppCampaignObjective(string objective) =>
        objective.Equals("OUTCOME_ENGAGEMENT", StringComparison.OrdinalIgnoreCase) ||
        objective.Equals("OUTCOME_SALES", StringComparison.OrdinalIgnoreCase) ||
        objective.Equals("OUTCOME_TRAFFIC", StringComparison.OrdinalIgnoreCase) ||
        objective.Equals("OUTCOME_LEADS", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetSpecialAdCategories(JsonObject campaignBody, out List<string> categories)
    {
        categories = [];
        if (campaignBody["special_ad_categories"] is not JsonArray arr)
            return false;

        foreach (var item in arr)
        {
            var value = item?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(value) ||
                value.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            categories.Add(value);
        }

        return true;
    }

    private static List<string> TryGetGeoCountryCodes(JsonObject adSetBody)
    {
        if (adSetBody["targeting"] is not JsonObject targeting ||
            targeting["geo_locations"] is not JsonObject geo ||
            geo["countries"] is not JsonArray countries)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var item in countries)
        {
            var code = item?.GetValue<string>()?.Trim().ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(code))
                result.Add(code);
        }

        return result;
    }

    private static bool HasNonEmptyStringArray(JsonObject body, string key)
    {
        if (body[key] is not JsonArray arr || arr.Count == 0)
            return false;

        return arr.Any(item => !string.IsNullOrWhiteSpace(item?.GetValue<string>()));
    }

    private static JsonArray ToJsonStringArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values.Distinct(StringComparer.OrdinalIgnoreCase))
            array.Add(value.ToUpperInvariant());

        return array;
    }

    /// <summary>
    /// Meta rejects overlapping geo (e.g. country US + city with radius inside US).
    /// When cities/regions/zips are set, drop <c>countries</c> before posting.
    /// </summary>
    private static void SanitizeAdSetGeoTargeting(JsonObject adSetBody)
    {
        if (adSetBody["targeting"] is not JsonObject targeting)
            return;
        if (targeting["geo_locations"] is not JsonObject geo)
            return;

        var hasSubCountryTargeting =
            HasGeoArrayEntries(geo, "cities") ||
            HasGeoArrayEntries(geo, "regions") ||
            HasGeoArrayEntries(geo, "zips") ||
            HasGeoArrayEntries(geo, "custom_locations");

        if (hasSubCountryTargeting && geo.ContainsKey("countries"))
            geo.Remove("countries");
    }

    private static bool HasGeoArrayEntries(JsonObject geo, string key)
    {
        return geo[key] is JsonArray arr && arr.Count > 0;
    }

    private static void ValidateCreativeDestinationUrl(JsonObject creativeBody)
    {
        var link = TryGetNestedString(creativeBody, "object_story_spec", "link_data", "link");
        if (string.IsNullOrWhiteSpace(link))
            return;

        if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not "http" and not "https")
        {
            throw new ArgumentException(
                "creative.objectStorySpec.linkData.link must be a valid absolute http or https URL.");
        }

        var host = uri.Host.Trim();
        if (IsNonPublicAdDestinationHost(host))
        {
            throw new ArgumentException(
                "Meta cannot use localhost or private network URLs in ads. Use a public https URL (staging or production).");
        }
    }

    private static bool IsNonPublicAdDestinationHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return true;

        var h = host.Trim().Trim('[', ']').ToLowerInvariant();
        if (h is "localhost" or "127.0.0.1" or "::1" ||
            h.EndsWith(".localhost", StringComparison.Ordinal) ||
            h.EndsWith(".local", StringComparison.Ordinal))
        {
            return true;
        }

        if (!IPAddress.TryParse(h, out var ip))
            return false;

        if (IPAddress.IsLoopback(ip))
            return true;

        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var b = ip.GetAddressBytes();
        if (b[0] == 10)
            return true;
        if (b[0] == 192 && b[1] == 168)
            return true;
        return b[0] == 172 && b[1] >= 16 && b[1] <= 31;
    }

    /// <summary>Meta city radius: 10–50 miles or 17–80 km per Marketing API docs.</summary>
    private static void ValidateAdSetGeoTargeting(JsonObject adSetBody)
    {
        if (adSetBody["targeting"] is not JsonObject targeting)
            return;
        if (targeting["geo_locations"] is not JsonObject geo)
            return;
        if (geo["cities"] is not JsonArray cities)
            return;

        foreach (var cityNode in cities)
        {
            if (cityNode is not JsonObject city || !city.ContainsKey("radius"))
                continue;

            var radius = city["radius"]!.GetValue<double>();
            var unit = city.TryGetPropertyValue("distance_unit", out var unitNode)
                ? unitNode?.GetValue<string>()?.Trim().ToLowerInvariant()
                : "mile";

            var isKm = unit is "kilometer" or "kilometre" or "km";
            var min = isKm ? 17 : 10;
            var max = isKm ? 80 : 50;
            var unitLabel = isKm ? "kilometers" : "miles";

            if (radius < min || radius > max)
            {
                throw new ArgumentException(
                    $"City targeting radius must be between {min} and {max} {unitLabel} (Meta limits). Received {radius}.");
            }
        }
    }

    private static void ValidateGraphPayloads(
        JsonObject campaignBody,
        JsonObject adSetBody,
        JsonObject creativeBody,
        JsonObject adBody)
    {
        if (!campaignBody.ContainsKey("name") ||
            string.IsNullOrWhiteSpace(campaignBody["name"]?.GetValue<string>()))
        {
            throw new ArgumentException("campaign.name is required.");
        }

        if (!campaignBody.ContainsKey("objective") ||
            string.IsNullOrWhiteSpace(campaignBody["objective"]?.GetValue<string>()))
        {
            throw new ArgumentException("campaign.objective is required.");
        }

        if (!adSetBody.ContainsKey("billing_event") ||
            string.IsNullOrWhiteSpace(adSetBody["billing_event"]?.GetValue<string>()))
        {
            throw new ArgumentException("adSet.billingEvent is required.");
        }

        if (!adSetBody.ContainsKey("optimization_goal") ||
            string.IsNullOrWhiteSpace(adSetBody["optimization_goal"]?.GetValue<string>()))
        {
            throw new ArgumentException("adSet.optimizationGoal is required.");
        }

        if (!adSetBody.ContainsKey("targeting"))
            throw new ArgumentException("adSet.targeting is required.");

        // At least one budget control must be present.
        var hasBudget =
            adSetBody.ContainsKey("daily_budget") ||
            adSetBody.ContainsKey("lifetime_budget") ||
            adSetBody.ContainsKey("spend_cap");

        if (!hasBudget)
            throw new ArgumentException("adSet must include dailyBudget, lifetimeBudget or spendCap.");

        if (!creativeBody.ContainsKey("object_story_spec"))
            throw new ArgumentException("creative.objectStorySpec is required.");
    }

    private static string? ResolveImageHashFromCreativeBody(JsonObject creativeBody)
    {
        if (creativeBody["object_story_spec"] is not JsonObject story)
            return null;
        if (story["link_data"] is not JsonObject link)
            return null;
        if (link["image_hash"] is not JsonValue hashNode)
            return null;

        var hash = hashNode.GetValue<string>();
        return string.IsNullOrWhiteSpace(hash) ? null : hash.Trim();
    }

    private JsonObject BuildCampaignGraphPayload(CreateMetaCampaignRequest request)
    {
        throw new NotSupportedException("Legacy payloads removed. Use request.campaign instead.");
    }

    private JsonObject BuildAdSetGraphPayload(CreateMetaCampaignRequest request)
    {
        throw new NotSupportedException("Legacy payloads removed. Use request.adSet instead.");
    }

    private static JsonObject BuildCreativeGraphPayload(CreateMetaCampaignRequest request, string defaultPageId)
    {
        throw new NotSupportedException("Legacy payloads removed. Use request.creative instead.");
    }

    private static JsonObject BuildAdGraphPayload(CreateMetaCampaignRequest request)
    {
        throw new NotSupportedException("Legacy payloads removed. Use request.ad instead.");
    }

    private static string? ResolveImageHash(CreateMetaCampaignRequest request, JsonObject creativeBody)
    {
        throw new NotSupportedException("Legacy image_hash resolution removed. Use creative.object_story_spec.link_data.image_hash.");
    }

    private static decimal? TryGetDailyBudgetDollars(JsonObject adSetBody)
    {
        if (!adSetBody.TryGetPropertyValue("daily_budget", out var budgetNode) || budgetNode == null)
            return null;

        try
        {
            var cents = budgetNode.GetValue<long>();
            return cents / 100m;
        }
        catch
        {
            return null;
        }
    }

    private static string MapMetaStatusToSourcingStatus(string metaStatus)
    {
        if (string.Equals(metaStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            return "active";
        if (string.Equals(metaStatus, "PAUSED", StringComparison.OrdinalIgnoreCase))
            return "paused";
        return metaStatus.Trim().ToLowerInvariant();
    }

    private static MarketingMetaCampaignRow BuildLocalMarketingRow(
        Guid orgId,
        CreateMetaCampaignRequest request,
        JsonObject campaignBody,
        JsonObject creativeBody,
        string? imageHash,
        string campaignId,
        string adSetId,
        string creativeId,
        string adId)
    {
        var campaignName = campaignBody["name"]?.GetValue<string>() ?? "Campaign";
        var destinationUrl = TryGetNestedString(creativeBody, "object_story_spec", "link_data", "link")
            ?? string.Empty;
        var adText = TryGetNestedString(creativeBody, "object_story_spec", "link_data", "message")
            ?? string.Empty;

        return new MarketingMetaCampaignRow
        {
            Id = Guid.NewGuid(),
            TenantId = orgId.ToString("D"),
            JobId = request.JobId,
            CampaignName = campaignName,
            DestinationType = string.IsNullOrWhiteSpace(request.DestinationType)
                ? "job_post_url"
                : request.DestinationType.Trim().ToLowerInvariant(),
            DestinationUrl = destinationUrl,
            WhatsappMessage = string.IsNullOrWhiteSpace(request.WhatsappMessage) ? null : request.WhatsappMessage.Trim(),
            AdText = adText,
            ImageHash = imageHash ?? string.Empty,
            MetaCampaignId = campaignId,
            MetaAdsetId = adSetId,
            MetaCreativeId = creativeId,
            MetaAdId = adId,
            Status = campaignBody["status"]?.GetValue<string>() ?? "PAUSED"
        };
    }

    public async Task DeleteMetaCampaignNodeAsync(
        Guid orgId,
        string metaCampaignId,
        CancellationToken cancellationToken = default)
    {
        var meta = await ResolveMetaAdsContextAsync(orgId, cancellationToken);
        var nodeId = metaCampaignId.Trim();
        if (string.IsNullOrEmpty(nodeId))
            return;

        var client = _httpClientFactory.CreateClient(HttpClientName);
        var url = BuildGraphUrl(meta.ApiVersion, nodeId, meta.AccessToken);

        _logger.LogInformation("Deleting Meta campaign/object {NodeId} via Graph API.", nodeId);

        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        using var response = await client.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.IsSuccessStatusCode)
            return;

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogInformation("Meta object {NodeId} already removed (404).", nodeId);
            return;
        }

        if (TryTreatMetaDeleteAsAlreadyGone(payload))
        {
            _logger.LogInformation("Meta object {NodeId} already removed (Graph error).", nodeId);
            return;
        }

        throw await ParseMetaErrorAsync(response, payload, cancellationToken);
    }

    private sealed record CampaignActionTarget(
        Guid? LocalRecordId,
        string MetaCampaignId,
        string? MetaAdSetId,
        string? MetaAdId);

    private async Task<CampaignActionTarget> ResolveCampaignActionTargetAsync(
        Guid orgId,
        string campaignRef,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(campaignRef))
            throw new ArgumentException("campaignRef is required.");

        var reference = campaignRef.Trim();
        var tenantId = orgId.ToString("D");
        var metaCampaignId = TryNormalizeMetaCampaignId(reference);
        MarketingMetaCampaignRow? row = null;
        MarketingMetaCampaignRow? rowWrongTenant = null;

        if (Guid.TryParse(reference, out var parsedGuid))
        {
            row = await _marketingRepo.GetByIdForTenantAsync(parsedGuid, tenantId, cancellationToken)
                  ?? await _marketingRepo.GetLatestByJobIdForTenantAsync(parsedGuid, tenantId, cancellationToken);

            if (row == null)
                rowWrongTenant = await _marketingRepo.GetByIdAsync(parsedGuid, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(metaCampaignId))
        {
            row = await _marketingRepo.GetByMetaCampaignIdForTenantAsync(
                metaCampaignId, tenantId, cancellationToken);
            if (row == null)
            {
                rowWrongTenant = await _marketingRepo.GetByMetaCampaignIdAsync(
                    metaCampaignId, cancellationToken);
            }
        }

        if (row != null)
        {
            if (string.IsNullOrWhiteSpace(row.MetaCampaignId))
            {
                throw new ArgumentException(
                    "Marketing Meta campaign record has no meta_campaign_id stored.");
            }

            return new CampaignActionTarget(
                row.Id,
                row.MetaCampaignId,
                row.MetaAdsetId,
                row.MetaAdId);
        }

        // Row exists but tenant_id in DB does not match JWT org — still pause/delete on Meta.
        if (rowWrongTenant != null && !string.IsNullOrWhiteSpace(rowWrongTenant.MetaCampaignId))
        {
            var wrongTenantMetaId = rowWrongTenant.MetaCampaignId;
            var useMetaOnly =
                !string.IsNullOrWhiteSpace(metaCampaignId) ||
                (Guid.TryParse(reference, out _) && rowWrongTenant.Id.ToString("D").Equals(reference, StringComparison.OrdinalIgnoreCase));

            if (useMetaOnly)
            {
                return new CampaignActionTarget(
                    null,
                    wrongTenantMetaId,
                    rowWrongTenant.MetaAdsetId,
                    rowWrongTenant.MetaAdId);
            }
        }

        if (!string.IsNullOrWhiteSpace(metaCampaignId))
        {
            return new CampaignActionTarget(null, metaCampaignId, null, null);
        }

        if (rowWrongTenant != null)
        {
            throw new ArgumentException(
                $"Campaign exists in the database but tenant_id '{rowWrongTenant.TenantId}' does not match your session org '{tenantId}'. " +
                $"Fix: UPDATE marketing_meta_campaigns SET tenant_id = '{tenantId}' WHERE id = '{rowWrongTenant.Id}', " +
                "or pause using Meta campaignId: " + rowWrongTenant.MetaCampaignId);
        }

        throw new ArgumentException(
            $"No Meta campaign found for '{reference}'. Use localRecordId, Meta campaignId ({metaCampaignId ?? "numeric"}), or jobId.");
    }

    private static string? TryNormalizeMetaCampaignId(string reference)
    {
        var trimmed = reference.Trim();
        if (trimmed.StartsWith("act_", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[4..];

        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        return digits.Length >= 5 ? digits : null;
    }

    private async Task UpdateMetaNodeStatusAsync(
        HttpClient client,
        string version,
        string nodeId,
        string accessToken,
        string status,
        CancellationToken cancellationToken)
    {
        var id = nodeId.Trim();
        if (string.IsNullOrEmpty(id))
            return;

        var body = new JsonObject { ["status"] = status };
        var url = BuildGraphUrl(version, id, accessToken);

        using var content = new StringContent(body.ToJsonString(_jsonSnake), Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

        _logger.LogInformation("Meta Graph status update {NodeId} -> {Status}", id, status);

        using var response = await client.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw await ParseMetaErrorAsync(response, payload, cancellationToken);

        using var doc = JsonDocument.Parse(payload);
        if (doc.RootElement.TryGetProperty("error", out _))
            throw await ParseMetaErrorFromBodyAsync(response, payload, cancellationToken);
    }

    public async Task<ResolveMetaGeoResponse> ResolveGeoAsync(
        Guid orgId,
        ResolveMetaGeoRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("query is required.");

        var meta = await ResolveMetaAdsContextAsync(orgId, cancellationToken);
        var version = meta.ApiVersion;
        var client = _httpClientFactory.CreateClient(HttpClientName);

        var q = request.Query.Trim();
        var limit = request.Limit is > 0 ? request.Limit.Value : 10;
        var countryCode = string.IsNullOrWhiteSpace(request.CountryCode) ? null : request.CountryCode.Trim().ToUpperInvariant();
        var locationTypes = (request.LocationTypes ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();

        // Meta Targeting Search: /search?type=adgeolocation&q=...
        // We ask for common fields used by geo targeting.
        var fields = Uri.EscapeDataString("key,name,type,country_code,country_name,region");
        var url = $"https://graph.facebook.com/{version}/search" +
                  $"?type=adgeolocation" +
                  $"&q={Uri.EscapeDataString(q)}" +
                  $"&limit={limit}" +
                  $"&fields={fields}";

        if (!string.IsNullOrWhiteSpace(countryCode))
            url += $"&country_code={Uri.EscapeDataString(countryCode)}";

        if (locationTypes.Length > 0)
        {
            // Meta expects JSON array string.
            var json = JsonSerializer.Serialize(locationTypes);
            url += $"&location_types={Uri.EscapeDataString(json)}";
        }

        url += $"&access_token={Uri.EscapeDataString(meta.AccessToken)}";

        using var httpReq = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await client.SendAsync(httpReq, cancellationToken);
        var payload = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw await ParseMetaErrorAsync(resp, payload, cancellationToken);

        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return new ResolveMetaGeoResponse();

        var list = new List<MetaGeoCandidate>();
        foreach (var item in data.EnumerateArray())
        {
            var key = item.TryGetProperty("key", out var k) ? k.GetString() : null;
            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(name))
                continue;

            list.Add(new MetaGeoCandidate
            {
                Key = key!,
                Name = name!,
                Type = item.TryGetProperty("type", out var t) ? t.GetString() : null,
                CountryCode = item.TryGetProperty("country_code", out var cc) ? cc.GetString() : null,
                CountryName = item.TryGetProperty("country_name", out var cn) ? cn.GetString() : null,
                Region = item.TryGetProperty("region", out var r) ? r.GetString() : null
            });
        }

        // Best: prefer exact case-insensitive name match, else first result.
        var best = list.FirstOrDefault(c => string.Equals(c.Name, q, StringComparison.OrdinalIgnoreCase))
                   ?? list.FirstOrDefault();

        return new ResolveMetaGeoResponse
        {
            Candidates = list,
            Best = best
        };
    }

    public async Task DeleteMarketingCampaignRecordAsync(
        Guid orgId,
        string campaignRef,
        CancellationToken cancellationToken = default)
    {
        var target = await ResolveCampaignActionTargetAsync(orgId, campaignRef, cancellationToken);

        if (!string.IsNullOrWhiteSpace(target.MetaCampaignId))
        {
            try
            {
                await DeleteMetaCampaignNodeAsync(orgId, target.MetaCampaignId, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Meta Ads not configured; removing local marketing row {LocalId} only.",
                    target.LocalRecordId);
            }
            catch (MetaGraphApiException)
            {
                throw;
            }
        }

        var localId = target.LocalRecordId;
        if (localId is null && Guid.TryParse(campaignRef.Trim(), out var parsedLocalId))
            localId = parsedLocalId;

        if (localId is not Guid id)
            return;

        var tenantId = orgId.ToString("D");
        var marketingRemoved = await _marketingRepo.DeleteByIdForTenantAsync(id, tenantId, cancellationToken);
        if (!marketingRemoved)
            marketingRemoved = await _marketingRepo.DeleteByIdAsync(id, cancellationToken);

        if (!marketingRemoved)
        {
            _logger.LogWarning(
                "No marketing_meta_campaigns row deleted for LocalId={LocalId} (may already be gone).",
                id);
        }

        var sourcingRemoved = await _sourcingRepository.DeleteCampaignAsync(orgId, id);
        if (!sourcingRemoved)
        {
            throw new InvalidOperationException(
                $"Meta campaign was removed remotely but sourcing_campaigns row {id} could not be deleted.");
        }
    }

    private static bool TryTreatMetaDeleteAsAlreadyGone(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("error", out var err))
                return false;
            if (err.TryGetProperty("code", out var code) && code.TryGetInt32(out var c) && c == 100)
                return true;
            var msg = err.TryGetProperty("message", out var m) ? m.GetString() : null;
            if (!string.IsNullOrEmpty(msg) &&
                msg.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private async Task<ResolvedMetaAdsContext> ResolveMetaAdsContextAsync(
        Guid orgId,
        CancellationToken cancellationToken)
    {
        var configJson = await _sourcingRepository.GetActiveSourceConnectionConfigJsonAsync(
            orgId,
            MetaSourceTypeCode,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(configJson))
        {
            throw new InvalidOperationException(
                $"Meta Ads is not configured for this organization. Connect source '{MetaSourceTypeCode}' with valid credentials.");
        }

        MetaSourceConnectionConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<MetaSourceConnectionConfig>(configJson, _jsonConfig);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Meta Ads connection config_json is invalid for source '{MetaSourceTypeCode}'.",
                ex);
        }

        if (config == null)
            throw new InvalidOperationException("Meta Ads connection config_json could not be parsed.");

        if (string.IsNullOrWhiteSpace(config.AccessToken))
            throw new InvalidOperationException("Meta Ads config_json is missing AccessToken.");
        if (string.IsNullOrWhiteSpace(config.AdAccountId))
            throw new InvalidOperationException("Meta Ads config_json is missing AdAccountId.");
        if (string.IsNullOrWhiteSpace(config.PageId))
            throw new InvalidOperationException("Meta Ads config_json is missing PageId.");

        return new ResolvedMetaAdsContext(
            config.AccessToken.Trim(),
            NormalizeAdAccountId(config.AdAccountId),
            config.PageId.Trim(),
            string.IsNullOrWhiteSpace(config.WhatsappPhoneNumber) ? null : config.WhatsappPhoneNumber.Trim(),
            NormalizeApiVersion(DefaultGraphApiVersion));
    }

    private static bool TenantMatchesOrg(string tenantId, Guid orgId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return false;
        var t = tenantId.Trim();
        if (Guid.TryParse(t, out var g))
            return g == orgId;
        return string.Equals(t, orgId.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task VerifyImageHashExistsAsync(
        HttpClient client,
        string version,
        string adAccountId,
        string accessToken,
        string imageHash,
        CancellationToken cancellationToken)
    {
        var hashesParam = Uri.EscapeDataString($"[\"{imageHash}\"]");
        var path = $"{adAccountId}/adimages?fields=hash,url&hashes={hashesParam}";
        var url = BuildGraphUrl(version, path, accessToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await client.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw await ParseMetaErrorAsync(response, payload, cancellationToken);

        using var doc = JsonDocument.Parse(payload);
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
            return;

        throw new ArgumentException("imageHash was not found in the Meta ad account. Upload the image first.");
    }

    private Task<string> PostJsonForIdAsync<T>(
        HttpClient client,
        string version,
        string adAccountId,
        string accessToken,
        string subPath,
        T body,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(body, _jsonSnake);
        return PostJsonForIdAsync(client, version, adAccountId, accessToken, subPath, json, cancellationToken);
    }

    private async Task<string> PostJsonForIdAsync(
        HttpClient client,
        string version,
        string adAccountId,
        string accessToken,
        string subPath,
        JsonObject body,
        CancellationToken cancellationToken)
    {
        var json = body.ToJsonString(_jsonSnake);
        return await PostJsonForIdAsync(client, version, adAccountId, accessToken, subPath, json, cancellationToken);
    }

    private async Task<string> PostJsonForIdAsync(
        HttpClient client,
        string version,
        string adAccountId,
        string accessToken,
        string subPath,
        string json,
        CancellationToken cancellationToken)
    {
        var relative = $"{adAccountId}/{subPath}";
        var url = BuildGraphUrl(version, relative, accessToken);

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

        _logger.LogDebug("Meta Graph POST {Path} body={Body}", relative, json);

        using var response = await client.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw await ParseMetaErrorAsync(response, payload, cancellationToken);

        using var doc = JsonDocument.Parse(payload);
        if (doc.RootElement.TryGetProperty("error", out _))
            throw await ParseMetaErrorFromBodyAsync(response, payload, cancellationToken);

        if (!doc.RootElement.TryGetProperty("id", out var idProp))
            throw new MetaGraphApiException("Meta response did not include id.", (int)response.StatusCode);

        var id = idProp.GetString();
        if (string.IsNullOrEmpty(id))
            throw new MetaGraphApiException("Meta response id was empty.", (int)response.StatusCode);

        return id;
    }

    private static Task<MetaGraphApiException> ParseMetaErrorAsync(
        HttpResponseMessage response,
        string payload,
        CancellationToken cancellationToken)
    {
        return ParseMetaErrorFromBodyAsync(response, payload, cancellationToken);
    }

    private static Task<MetaGraphApiException> ParseMetaErrorFromBodyAsync(
        HttpResponseMessage response,
        string payload,
        CancellationToken _)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                var message = err.TryGetProperty("message", out var m) ? m.GetString() ?? "Meta API error" : "Meta API error";
                var code = err.TryGetProperty("code", out var c) && c.TryGetInt32(out var ic) ? ic : (int?)null;
                var title = err.TryGetProperty("error_user_title", out var t) ? t.GetString() : null;
                var msg = err.TryGetProperty("error_user_msg", out var um) ? um.GetString() : null;
                var status = (int)response.StatusCode;
                return Task.FromResult(new MetaGraphApiException(
                    message,
                    status,
                    code,
                    title,
                    msg));
            }
        }
        catch
        {
            // fall through
        }

        return Task.FromResult(new MetaGraphApiException(
            string.IsNullOrWhiteSpace(payload) ? response.ReasonPhrase ?? "Meta request failed" : payload.Trim(),
            (int)response.StatusCode));
    }

    private static string BuildGraphUrl(string version, string path, string accessToken)
    {
        var basePath = path.StartsWith('/') ? path[1..] : path;
        var url = $"https://graph.facebook.com/{version}/{basePath}";
        var sep = url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{url}{sep}access_token={Uri.EscapeDataString(accessToken.Trim())}";
    }

    private static string NormalizeAdAccountId(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith("act_", StringComparison.OrdinalIgnoreCase))
            return "act_" + s.AsSpan(4).ToString();
        return "act_" + s;
    }

    private static string NormalizeApiVersion(string raw)
    {
        var v = raw.Trim();
        if (string.IsNullOrEmpty(v))
            return "v23.0";
        return v.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? v : "v" + v;
    }

    private static string BuildAdsManagerUrl(string adAccountId)
    {
        var id = adAccountId.StartsWith("act_", StringComparison.OrdinalIgnoreCase)
            ? adAccountId[4..]
            : adAccountId;
        return $"https://adsmanager.facebook.com/adsmanager/manage/ads?act={Uri.EscapeDataString(id)}";
    }

    private Uri GetAzureOpenAiResourceUri()
        => GetAzureOpenAiResourceUriFor("AzureOpenAI:Resource");

    private Uri GetAzureOpenAiResourceUriFor(string preferredKey)
    {
        var preferred = _configuration[preferredKey]?.Trim();
        if (!string.IsNullOrEmpty(preferred))
            return new Uri(preferred.TrimEnd('/') + "/");

        var resource = _configuration["AzureOpenAI:Resource"]?.Trim();
        if (!string.IsNullOrEmpty(resource))
            return new Uri(resource.TrimEnd('/') + "/");

        var ep = _configuration["AzureOpenAI:Endpoint"]?.Trim();
        if (string.IsNullOrEmpty(ep))
            throw new InvalidOperationException("Azure Open AI is not configured (AzureOpenAI:Resource or Endpoint).");

        var uri = new Uri(ep);
        return new Uri($"{uri.Scheme}://{uri.Authority}/");
    }

    private async Task<string> BuildDallePromptFromJobAsync(
        string title,
        string description,
        CancellationToken cancellationToken)
    {
        var apiKey = _configuration["AzureOpenAI:ChatApiKey"]
            ?? _configuration["AzureOpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("AzureOpenAI:ChatApiKey (or AzureOpenAI:ApiKey) is missing.");

        var deployment = _configuration["AzureOpenAI:ChatDeployment"]
            ?? _configuration["AzureOpenAI:Deployment"]
            ?? "gpt-4o-mini";

        try
        {
            var client = new AzureOpenAIClient(
                GetAzureOpenAiResourceUriFor("AzureOpenAI:ChatResource"),
                new AzureKeyCredential(apiKey));
            var chat = client.GetChatClient(deployment);

            var userContent = $"Job title:\n{title}\n\nJob description:\n{description}";
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(DallePromptSystemMessage),
                new UserChatMessage(userContent)
            };

            var completion = await chat.CompleteChatAsync(
                messages,
                new ChatCompletionOptions
                {
                    MaxOutputTokenCount = 700,
                    Temperature = 0.65f
                },
                cancellationToken);

            var text = completion.Value.Content.FirstOrDefault()?.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }
        catch (Exception ex) when (
            ex.Message.Contains("DeploymentNotFound", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("invalid subscription key", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                ex,
                "Chat deployment {Deployment} was not found. Falling back to deterministic image prompt.",
                deployment);
        }

        return BuildFallbackPrompt(title, description);
    }


    private static string BuildFallbackPrompt(string title, string description)
    {
        var cleanDesc = description.Replace("\r", " ").Replace("\n", " ").Trim();
        if (cleanDesc.Length > 900)
            cleanDesc = cleanDesc[..900] + "...";

        return
            $"Create a professional recruiting ad image in square 1:1 composition (1024x1024), modern visual style, inclusive workforce, bright natural lighting, realistic but polished. Include large, readable ad text headline exactly: 'WE ARE HIRING: {title}'. Add a short supporting line such as 'Apply now'. Context from job description: {cleanDesc}. No logos, no watermarks, no UI screenshots, no brand names.";
    }

    private async Task<(string B64Json, string? RevisedPrompt)> CallAzureImageGenerationsAsync(
        string prompt,
        string size,
        CancellationToken cancellationToken)
    {
        var apiKey = _configuration["AzureOpenAI:ImageApiKey"]
            ?? _configuration["AzureOpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("AzureOpenAI:ImageApiKey (or AzureOpenAI:ApiKey) is missing.");

        var deployment = _configuration["AzureOpenAI:ImageDeployment"]?.Trim();
        if (string.IsNullOrWhiteSpace(deployment))
        {
            throw new InvalidOperationException(
                "AzureOpenAI:ImageDeployment is not set. Deploy a DALL·E 3 (or compatible) model in Azure OpenAI and set the deployment name.");
        }

        var apiVersion = _configuration["AzureOpenAI:ImagesApiVersion"]?.Trim() ?? "2024-02-01";
        var baseUri = GetAzureOpenAiResourceUri();
        var url =
            $"{baseUri}openai/deployments/{Uri.EscapeDataString(deployment)}/images/generations?api-version={Uri.EscapeDataString(apiVersion)}";

        var bodyObj = new
        {
            prompt,
            n = 1,
            size,
            quality = (_configuration["AzureOpenAI:ImageQuality"]?.Trim() ?? "high")
        };
        var json = JsonSerializer.Serialize(bodyObj);

        var http = _httpClientFactory.CreateClient(AzureOpenAiHttpClient);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("api-key", apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Image generation timed out after waiting for Azure OpenAI response. Try again or use manual upload.",
                ex);
        }

        using (response)
        {
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(payload) ? "{}" : payload);
        if (doc.RootElement.TryGetProperty("error", out var errEl))
        {
            var msg = errEl.TryGetProperty("message", out var m) ? m.GetString() ?? "Image generation failed" : "Image generation failed";
            throw new InvalidOperationException(msg);
        }

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Image generation failed ({(int)response.StatusCode}).");

        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            throw new InvalidOperationException("Image generation returned no data.");

        var first = data[0];
        var b64 = first.TryGetProperty("b64_json", out var b64El) ? b64El.GetString() : null;

        if (string.IsNullOrWhiteSpace(b64) && first.TryGetProperty("url", out var urlEl))
        {
            var imgUrl = urlEl.GetString();
            if (!string.IsNullOrWhiteSpace(imgUrl))
            {
                using var imgReq = new HttpRequestMessage(HttpMethod.Get, imgUrl);
                using var imgRes = await http.SendAsync(imgReq, cancellationToken);
                var bytes = await imgRes.Content.ReadAsByteArrayAsync(cancellationToken);
                if (imgRes.IsSuccessStatusCode && bytes.Length > 0)
                    b64 = Convert.ToBase64String(bytes);
            }
        }

        if (string.IsNullOrWhiteSpace(b64))
            throw new InvalidOperationException("Image generation returned empty image data.");

            var revised = first.TryGetProperty("revised_prompt", out var rp) ? rp.GetString() : null;
            return (b64, revised);
        }
    }

    private static (int Width, int Height) ParseSize(string size)
    {
        var parts = size.Split('x', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var w) &&
            int.TryParse(parts[1], out var h))
            return (w, h);

        return (1024, 1024);
    }

    private static string? TryGetNestedString(JsonObject root, params string[] path)
    {
        JsonNode? current = root;
        foreach (var segment in path)
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(segment, out current))
                return null;
        }

        return current is JsonValue value ? value.GetValue<string>() : null;
    }
}
