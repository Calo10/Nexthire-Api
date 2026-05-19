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

    /// <summary>Create a paused Meta campaign, ad set, creative, and ad; persist local record.</summary>
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
