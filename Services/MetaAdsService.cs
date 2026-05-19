using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using nexthire_api.DTOs;
using nexthire_api.Options;
using nexthire_api.Repositories;
using OpenAI.Chat;

namespace nexthire_api.Services;

public class MetaAdsService : IMetaAdsService
{
    private const string HttpClientName = "MetaGraph";
    private const string AzureOpenAiHttpClient = "AzureOpenAI";

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
    private readonly MetaAdsOptions _options;
    private readonly IJobRepository _jobRepository;
    private readonly IMarketingMetaCampaignRepository _marketingRepo;
    private readonly ILogger<MetaAdsService> _logger;

    private readonly JsonSerializerOptions _jsonSnake = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public MetaAdsService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IOptions<MetaAdsOptions> options,
        IJobRepository jobRepository,
        IMarketingMetaCampaignRepository marketingRepo,
        ILogger<MetaAdsService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _options = options.Value;
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
        EnsureMetaConfigured();

        var adAccountId = NormalizeAdAccountId(_options.AdAccountId);
        var version = NormalizeApiVersion(_options.ApiVersion);
        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var fileContent = new StreamContent(stream);
        var mime = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(mime);

        using var form = new MultipartFormDataContent();
        form.Add(fileContent, "filename", fileName);

        var url = BuildGraphUrl(version, $"{adAccountId}/adimages", includeAccessToken: true);
        _logger.LogInformation(
            "Uploading Meta ad image for org {OrgId}. AdAccount={AdAccount}, FileName={FileName}, Length={Length}",
            orgId,
            adAccountId,
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
        EnsureMetaConfigured();
        ArgumentNullException.ThrowIfNull(request);

        if (!TenantMatchesOrg(request.TenantId, orgId))
            throw new ArgumentException("tenantId does not match the authenticated organization.");

        if (string.IsNullOrWhiteSpace(request.CampaignName))
            throw new ArgumentException("campaignName is required.");
        if (string.IsNullOrWhiteSpace(request.DestinationUrl))
            throw new ArgumentException("destinationUrl is required.");
        if (string.IsNullOrWhiteSpace(request.AdText))
            throw new ArgumentException("adText is required.");
        if (request.DailyBudget <= 0)
            throw new ArgumentException("dailyBudget must be greater than 0.");
        if (request.AgeMin > request.AgeMax)
            throw new ArgumentException("ageMin must be less than or equal to ageMax.");
        if (request.Platforms == null || request.Platforms.Length == 0)
            throw new ArgumentException("platforms must contain at least one entry.");

        foreach (var p in request.Platforms)
        {
            if (string.IsNullOrWhiteSpace(p) || !AllowedPublisherPlatforms.Contains(p.Trim()))
                throw new ArgumentException($"Invalid platform: {p}. Use facebook, instagram, audience_network, or messenger.");
        }

        if (string.IsNullOrWhiteSpace(request.ImageHash))
            throw new ArgumentException("imageHash is required.");

        var job = await _jobRepository.GetByIdAsync(orgId, request.JobId);
        if (job == null)
            throw new ArgumentException("jobId was not found for this organization.");

        var adAccountId = NormalizeAdAccountId(_options.AdAccountId);
        var version = NormalizeApiVersion(_options.ApiVersion);
        var client = _httpClientFactory.CreateClient(HttpClientName);

        await VerifyImageHashExistsAsync(client, version, adAccountId, request.ImageHash.Trim(), cancellationToken);

        var campaignName = request.CampaignName.Trim();
        var objective = string.IsNullOrWhiteSpace(request.Objective) ? "OUTCOME_TRAFFIC" : request.Objective.Trim();
        var country = request.Country.Trim().ToUpperInvariant();
        var platforms = request.Platforms.Select(p => p.Trim().ToLowerInvariant()).Distinct().ToArray();
        var destinationUrl = request.DestinationUrl.Trim();
        var adText = request.AdText.Trim();
        var ctaType = string.IsNullOrWhiteSpace(request.CtaType) ? "LEARN_MORE" : request.CtaType.Trim();

        _logger.LogInformation(
            "Creating Meta campaign for org {OrgId}, job {JobId}, name {Name}",
            orgId,
            request.JobId,
            campaignName);

        var campaignBody = new MetaGraphCampaignBody
        {
            Name = campaignName,
            Objective = objective,
            Status = "PAUSED",
            SpecialAdCategories = Array.Empty<string>(),
            IsAdsetBudgetSharingEnabled = false
        };

        var campaignId = await PostJsonForIdAsync(
            client,
            version,
            adAccountId,
            "campaigns",
            campaignBody,
            cancellationToken);

        var adSetBody = new MetaCreateAdSetRequest
        {
            Name = $"{campaignName} - AdSet",
            CampaignId = campaignId,
            BillingEvent = "IMPRESSIONS",
            OptimizationGoal = "LINK_CLICKS",
            DailyBudget = request.DailyBudget,
            BidStrategy = "LOWEST_COST_WITHOUT_CAP",
            Targeting = new MetaTargeting
            {
                GeoLocations = new MetaGeoLocations { Countries = new[] { country } },
                AgeMin = request.AgeMin,
                AgeMax = request.AgeMax,
                TargetingAutomation = new MetaTargetingAutomation { AdvantageAudience = 0 },
                PublisherPlatforms = platforms
            },
            Status = "PAUSED"
        };

        var adSetId = await PostJsonForIdAsync(
            client,
            version,
            adAccountId,
            "adsets",
            adSetBody,
            cancellationToken);

        var creativeBody = new MetaCreateCreativeRequest
        {
            Name = $"{campaignName} - Creative",
            ObjectStorySpec = new MetaObjectStorySpec
            {
                PageId = _options.PageId.Trim(),
                LinkData = new MetaLinkData
                {
                    Message = adText,
                    Link = destinationUrl,
                    ImageHash = request.ImageHash.Trim(),
                    CallToAction = new MetaCallToAction { Type = ctaType }
                }
            }
        };

        var creativeId = await PostJsonForIdAsync(
            client,
            version,
            adAccountId,
            "adcreatives",
            creativeBody,
            cancellationToken);

        var adBody = new MetaCreateAdRequest
        {
            Name = $"{campaignName} - Ad",
            AdsetId = adSetId,
            Creative = new MetaCreativeRef { CreativeId = creativeId },
            Status = "PAUSED"
        };

        var adId = await PostJsonForIdAsync(
            client,
            version,
            adAccountId,
            "ads",
            adBody,
            cancellationToken);

        var adsManagerUrl = BuildAdsManagerUrl(adAccountId);

        var row = new MarketingMetaCampaignRow
        {
            Id = Guid.NewGuid(),
            TenantId = request.TenantId.Trim(),
            JobId = request.JobId,
            CampaignName = campaignName,
            DestinationType = request.DestinationType.Trim().ToLowerInvariant(),
            DestinationUrl = destinationUrl,
            WhatsappMessage = string.IsNullOrWhiteSpace(request.WhatsappMessage) ? null : request.WhatsappMessage.Trim(),
            AdText = adText,
            ImageHash = request.ImageHash.Trim(),
            MetaCampaignId = campaignId,
            MetaAdsetId = adSetId,
            MetaCreativeId = creativeId,
            MetaAdId = adId,
            Status = "PAUSED"
        };

        await _marketingRepo.InsertAsync(row, cancellationToken);

        _logger.LogInformation(
            "Meta campaign created and stored. LocalId={LocalId}, MetaCampaignId={MetaCampaignId}",
            row.Id,
            campaignId);

        return new CreateMetaCampaignResponse
        {
            CampaignId = campaignId,
            AdSetId = adSetId,
            CreativeId = creativeId,
            AdId = adId,
            AdAccountId = adAccountId,
            AdsManagerUrl = adsManagerUrl
        };
    }

    public async Task DeleteMetaCampaignNodeAsync(string metaCampaignId, CancellationToken cancellationToken = default)
    {
        EnsureMetaConfigured();
        var nodeId = metaCampaignId.Trim();
        if (string.IsNullOrEmpty(nodeId))
            return;

        var version = NormalizeApiVersion(_options.ApiVersion);
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var url = BuildGraphUrl(version, nodeId, includeAccessToken: true);

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

    public async Task DeleteMarketingCampaignRecordAsync(
        Guid orgId,
        Guid localRecordId,
        CancellationToken cancellationToken = default)
    {
        var tenantId = orgId.ToString("D");
        var row = await _marketingRepo.GetByIdForTenantAsync(localRecordId, tenantId, cancellationToken);
        if (row == null)
            throw new ArgumentException("Marketing Meta campaign record was not found for this organization.");

        if (!string.IsNullOrWhiteSpace(row.MetaCampaignId))
        {
            try
            {
                await DeleteMetaCampaignNodeAsync(row.MetaCampaignId, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Meta Ads not configured; removing local marketing row {LocalId} only.",
                    localRecordId);
            }
            catch (MetaGraphApiException)
            {
                throw;
            }
        }

        var removed = await _marketingRepo.DeleteByIdForTenantAsync(localRecordId, tenantId, cancellationToken);
        if (!removed)
            throw new InvalidOperationException("Failed to remove local marketing campaign row.");
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

    private void EnsureMetaConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.AccessToken))
            throw new InvalidOperationException("Meta Ads is not configured: MetaAds:AccessToken is missing.");
        if (string.IsNullOrWhiteSpace(_options.AdAccountId))
            throw new InvalidOperationException("Meta Ads is not configured: MetaAds:AdAccountId is missing.");
        if (string.IsNullOrWhiteSpace(_options.PageId))
            throw new InvalidOperationException("Meta Ads is not configured: MetaAds:PageId is missing.");
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
        string imageHash,
        CancellationToken cancellationToken)
    {
        var hashesParam = Uri.EscapeDataString($"[\"{imageHash}\"]");
        var path = $"{adAccountId}/adimages?fields=hash,url&hashes={hashesParam}";
        var url = BuildGraphUrl(version, path, includeAccessToken: true);

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

    private async Task<string> PostJsonForIdAsync<T>(
        HttpClient client,
        string version,
        string adAccountId,
        string subPath,
        T body,
        CancellationToken cancellationToken)
    {
        var relative = $"{adAccountId}/{subPath}";
        var url = BuildGraphUrl(version, relative, includeAccessToken: true);
        var json = JsonSerializer.Serialize(body, _jsonSnake);

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

        _logger.LogDebug("Meta Graph POST {Path}", relative);

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

    private string BuildGraphUrl(string version, string path, bool includeAccessToken)
    {
        var basePath = path.StartsWith('/') ? path[1..] : path;
        var url = $"https://graph.facebook.com/{version}/{basePath}";
        if (!includeAccessToken)
            return url;

        var token = _options.AccessToken.Trim();
        var sep = url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{url}{sep}access_token={Uri.EscapeDataString(token)}";
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

    // --- Meta Graph JSON bodies (snake_case via serializer) ---

    private sealed class MetaGraphCampaignBody
    {
        public string Name { get; init; } = string.Empty;
        public string Objective { get; init; } = string.Empty;
        public string Status { get; init; } = "PAUSED";
        public string[] SpecialAdCategories { get; init; } = Array.Empty<string>();
        public bool IsAdsetBudgetSharingEnabled { get; init; }
    }

    private sealed class MetaCreateAdSetRequest
    {
        public string Name { get; init; } = string.Empty;
        public string CampaignId { get; init; } = string.Empty;
        public string BillingEvent { get; init; } = string.Empty;
        public string OptimizationGoal { get; init; } = string.Empty;
        public int DailyBudget { get; init; }
        public string BidStrategy { get; init; } = string.Empty;
        public MetaTargeting Targeting { get; init; } = null!;
        public string Status { get; init; } = "PAUSED";
    }

    private sealed class MetaTargeting
    {
        public MetaGeoLocations GeoLocations { get; init; } = null!;
        public int AgeMin { get; init; }
        public int AgeMax { get; init; }
        public MetaTargetingAutomation TargetingAutomation { get; init; } = null!;
        public string[] PublisherPlatforms { get; init; } = Array.Empty<string>();
    }

    private sealed class MetaGeoLocations
    {
        public string[] Countries { get; init; } = Array.Empty<string>();
    }

    private sealed class MetaTargetingAutomation
    {
        public int AdvantageAudience { get; init; }
    }

    private sealed class MetaCreateCreativeRequest
    {
        public string Name { get; init; } = string.Empty;
        public MetaObjectStorySpec ObjectStorySpec { get; init; } = null!;
    }

    private sealed class MetaObjectStorySpec
    {
        public string PageId { get; init; } = string.Empty;
        public MetaLinkData LinkData { get; init; } = null!;
    }

    private sealed class MetaLinkData
    {
        public string Message { get; init; } = string.Empty;
        public string Link { get; init; } = string.Empty;
        public string ImageHash { get; init; } = string.Empty;
        public MetaCallToAction CallToAction { get; init; } = null!;
    }

    private sealed class MetaCallToAction
    {
        public string Type { get; init; } = string.Empty;
    }

    private sealed class MetaCreateAdRequest
    {
        public string Name { get; init; } = string.Empty;
        public string AdsetId { get; init; } = string.Empty;
        public MetaCreativeRef Creative { get; init; } = null!;
        public string Status { get; init; } = "PAUSED";
    }

    private sealed class MetaCreativeRef
    {
        public string CreativeId { get; init; } = string.Empty;
    }
}
