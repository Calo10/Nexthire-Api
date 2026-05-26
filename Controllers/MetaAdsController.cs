using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

[ApiController]
[Route("api/marketing/meta")]
[Authorize]
public class MetaAdsController : ControllerBase
{
    private readonly IMetaAdsService _metaAdsService;
    private readonly ILogger<MetaAdsController> _logger;

    public MetaAdsController(IMetaAdsService metaAdsService, ILogger<MetaAdsController> logger)
    {
        _metaAdsService = metaAdsService;
        _logger = logger;
    }

    /// <summary>Upload a creative image to Meta and return image_hash.</summary>
    [HttpPost("creative-image")]
    [RequestSizeLimit(25 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 25 * 1024 * 1024)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(UploadMetaImageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> UploadCreativeImage(
        [FromForm] UploadMetaCreativeImageForm form,
        CancellationToken cancellationToken)
    {
        if (!TryGetOrgId(out var orgId, out var unauthorized))
            return unauthorized!;

        var file = form?.File;
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "file is required." });

        return await ExecuteMetaImageAction(
            ct => _metaAdsService.UploadCreativeImageAsync(orgId, file, ct),
            cancellationToken);
    }

    /// <summary>
    /// AI: from job title + description, build an image prompt and generate a square ad visual for UI preview (not sent to Meta).
    /// Client can upload the asset with POST creative-image to obtain image_hash.
    /// </summary>
    [HttpPost("creative-image/demo")]
    [ProducesResponseType(typeof(GenerateMetaCreativePreviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GenerateCreativePreview(
        [FromBody] GenerateMetaCreativePreviewRequest? request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        if (!TryGetOrgId(out var orgId, out var unauthorized))
            return unauthorized!;

        if (request == null || request.JobId == Guid.Empty)
            return BadRequest(new { error = "jobId is required." });

        try
        {
            var result = await _metaAdsService.GenerateCreativePreviewFromJobAsync(orgId, request.JobId, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Creative preview validation failed");
            return BadRequest(new { error = ex.Message });
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Creative preview generation timed out");
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Creative preview generation failed (configuration or OpenAI)");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
    }

    /// <summary>List rows from <c>marketing_meta_campaigns</c> (Meta Ads). Not <c>/api/sourcing/campaigns</c>.</summary>
    [HttpGet("campaigns")]
    [ProducesResponseType(typeof(IReadOnlyList<MetaMarketingCampaignDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListCampaigns(
        [FromQuery] Guid? jobId,
        CancellationToken cancellationToken)
    {
        if (!TryGetOrgId(out var orgId, out var unauthorized))
            return unauthorized!;

        var items = await _metaAdsService.ListMarketingCampaignsAsync(orgId, jobId, cancellationToken);
        return Ok(items);
    }

    /// <summary>Get one row from <c>marketing_meta_campaigns</c> by local record id.</summary>
    [HttpGet("campaigns/{localRecordId:guid}")]
    [ProducesResponseType(typeof(MetaMarketingCampaignDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCampaign(Guid localRecordId, CancellationToken cancellationToken)
    {
        if (!TryGetOrgId(out var orgId, out var unauthorized))
            return unauthorized!;

        var item = await _metaAdsService.GetMarketingCampaignAsync(orgId, localRecordId, cancellationToken);
        return item == null ? NotFound() : Ok(item);
    }

    /// <summary>
    /// Create Meta campaign, ad set, creative, and ad via Graph API.
    /// Supports legacy flat fields and/or full <c>campaign</c>, <c>adSet</c>, <c>creative</c>, <c>ad</c> payloads
    /// (any Meta Marketing API field via typed properties or ExtensionData).
    /// </summary>
    [HttpPost("campaigns")]
    [ProducesResponseType(typeof(CreateMetaCampaignResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> CreateCampaign([FromBody] CreateMetaCampaignRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        if (!TryGetOrgId(out var orgId, out var unauthorized))
            return unauthorized!;

        try
        {
            var result = await _metaAdsService.CreateCampaignAsync(orgId, request, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Meta campaign validation failed");
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Meta Ads configuration error");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
        catch (MetaGraphApiException ex)
        {
            _logger.LogWarning(
                ex,
                "Meta Graph error creating campaign. Status={Status}, Code={Code}",
                ex.HttpStatus,
                ex.MetaCode);
            return StatusCode(
                ex.HttpStatus is >= 400 and < 600 ? ex.HttpStatus : StatusCodes.Status502BadGateway,
                new
                {
                    error = ex.Message,
                    metaCode = ex.MetaCode,
                    metaErrorUserTitle = ex.MetaErrorUserTitle,
                    metaErrorUserMsg = ex.MetaErrorUserMsg
                });
        }
    }

    /// <summary>Activate campaign, ad set, and ad in Meta for a stored marketing record.</summary>
    /// <param name="campaignRef">Local record GUID (<c>localRecordId</c>) or Meta <c>campaignId</c>.</param>
    [HttpPost("campaigns/{campaignRef}/activate")]
    [ProducesResponseType(typeof(MetaMarketingCampaignActionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> ActivateCampaign(string campaignRef, CancellationToken cancellationToken)
    {
        if (!TryGetOrgId(out var orgId, out var unauthorized))
            return unauthorized!;

        return await ExecuteMetaCampaignAction(
            ct => _metaAdsService.ActivateMarketingCampaignAsync(orgId, campaignRef, ct),
            cancellationToken);
    }

    /// <summary>Pause campaign, ad set, and ad in Meta for a stored marketing record.</summary>
    /// <param name="campaignRef">Local record GUID (<c>localRecordId</c>) or Meta <c>campaignId</c>.</param>
    [HttpPost("campaigns/{campaignRef}/pause")]
    [ProducesResponseType(typeof(MetaMarketingCampaignActionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> PauseCampaign(string campaignRef, CancellationToken cancellationToken)
    {
        if (!TryGetOrgId(out var orgId, out var unauthorized))
            return unauthorized!;

        return await ExecuteMetaCampaignAction(
            ct => _metaAdsService.PauseMarketingCampaignAsync(orgId, campaignRef, ct),
            cancellationToken);
    }

    /// <summary>Delete the Meta campaign hierarchy and remove the local marketing record.</summary>
    /// <param name="campaignRef">Local record GUID (<c>localRecordId</c>) or Meta <c>campaignId</c>.</param>
    [HttpDelete("campaigns/{campaignRef}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> DeleteCampaign(string campaignRef, CancellationToken cancellationToken)
    {
        if (!TryGetOrgId(out var orgId, out var unauthorized))
            return unauthorized!;

        try
        {
            await _metaAdsService.DeleteMarketingCampaignRecordAsync(orgId, campaignRef, cancellationToken);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Meta campaign delete validation failed");
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Meta Ads configuration error");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
        catch (MetaGraphApiException ex)
        {
            _logger.LogWarning(
                ex,
                "Meta Graph error deleting campaign. Status={Status}, Code={Code}",
                ex.HttpStatus,
                ex.MetaCode);
            return StatusCode(
                ex.HttpStatus is >= 400 and < 600 ? ex.HttpStatus : StatusCodes.Status502BadGateway,
                new
                {
                    error = ex.Message,
                    metaCode = ex.MetaCode,
                    metaErrorUserTitle = ex.MetaErrorUserTitle,
                    metaErrorUserMsg = ex.MetaErrorUserMsg
                });
        }
    }

    /// <summary>
    /// Resolve a human location label (e.g. from Google Places) into Meta geo keys usable in ad set targeting.
    /// </summary>
    [HttpPost("geo/resolve")]
    [ProducesResponseType(typeof(ResolveMetaGeoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> ResolveGeo([FromBody] ResolveMetaGeoRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        if (!TryGetOrgId(out var orgId, out var unauthorized))
            return unauthorized!;

        try
        {
            var result = await _metaAdsService.ResolveGeoAsync(orgId, request, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Meta geo resolve validation failed");
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Meta Ads configuration error");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
        catch (MetaGraphApiException ex)
        {
            _logger.LogWarning(
                ex,
                "Meta Graph error resolving geo. Status={Status}, Code={Code}",
                ex.HttpStatus,
                ex.MetaCode);
            return StatusCode(
                ex.HttpStatus is >= 400 and < 600 ? ex.HttpStatus : StatusCodes.Status502BadGateway,
                new
                {
                    error = ex.Message,
                    metaCode = ex.MetaCode,
                    metaErrorUserTitle = ex.MetaErrorUserTitle,
                    metaErrorUserMsg = ex.MetaErrorUserMsg
                });
        }
    }

    private async Task<IActionResult> ExecuteMetaCampaignAction(
        Func<CancellationToken, Task<MetaMarketingCampaignActionResponse>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await action(cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Meta campaign action validation failed");
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Meta Ads configuration error");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
        catch (MetaGraphApiException ex)
        {
            _logger.LogWarning(
                ex,
                "Meta Graph error on campaign action. Status={Status}, Code={Code}",
                ex.HttpStatus,
                ex.MetaCode);
            return StatusCode(
                ex.HttpStatus is >= 400 and < 600 ? ex.HttpStatus : StatusCodes.Status502BadGateway,
                new
                {
                    error = ex.Message,
                    metaCode = ex.MetaCode,
                    metaErrorUserTitle = ex.MetaErrorUserTitle,
                    metaErrorUserMsg = ex.MetaErrorUserMsg
                });
        }
    }

    private async Task<IActionResult> ExecuteMetaImageAction(
        Func<CancellationToken, Task<UploadMetaImageResponse>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await action(cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Meta image upload validation failed");
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Meta Ads configuration error");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
        catch (MetaGraphApiException ex)
        {
            _logger.LogWarning(
                ex,
                "Meta Graph error on image upload. Status={Status}, Code={Code}",
                ex.HttpStatus,
                ex.MetaCode);
            return StatusCode(
                ex.HttpStatus is >= 400 and < 600 ? ex.HttpStatus : StatusCodes.Status502BadGateway,
                new
                {
                    error = ex.Message,
                    metaCode = ex.MetaCode,
                    metaErrorUserTitle = ex.MetaErrorUserTitle,
                    metaErrorUserMsg = ex.MetaErrorUserMsg
                });
        }
    }

    private bool TryGetOrgId(out Guid orgId, out IActionResult? unauthorized)
    {
        unauthorized = null;
        orgId = Guid.Empty;

        var orgClaim = User.FindFirst("nexa_org_id")?.Value;
        if (string.IsNullOrWhiteSpace(orgClaim) || !Guid.TryParse(orgClaim, out orgId))
        {
            unauthorized = Unauthorized(new { message = "Missing org context. Login again." });
            return false;
        }

        return true;
    }
}
