using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs;
using nexthire_api.Helpers;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

[ApiController]
[Route("api/integrations/whatsapp")]
public class WhatsAppIntegrationController : ControllerBase
{
    private readonly IWhatsAppInboundService _service;
    private readonly ILogger<WhatsAppIntegrationController> _logger;

    public WhatsAppIntegrationController(
        IWhatsAppInboundService service,
        ILogger<WhatsAppIntegrationController> logger)
    {
        _service = service;
        _logger = logger;
    }

    [HttpPost("inbound")]
    public async Task<IActionResult> Inbound(
        [FromBody] WhatsAppInboundMessageDto request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        if (string.IsNullOrWhiteSpace(request.TenantId))
            return BadRequest(new { message = "tenantId is required." });
        if (string.IsNullOrWhiteSpace(request.ProviderMessageId))
            return BadRequest(new { message = "providerMessageId is required." });
        if (string.IsNullOrWhiteSpace(request.From))
            return BadRequest(new { message = "from is required." });

        WhatsAppInboundMediaHelper.EnsureMediaPopulated(request);
        if (string.IsNullOrWhiteSpace(request.Body) && !WhatsAppInboundMediaHelper.HasInboundMedia(request))
            return BadRequest(new { message = "body or media is required." });

        request.Body ??= string.Empty;

        var bodyPreview = request.Body.Length <= 160
            ? request.Body
            : request.Body[..160] + "…";
        _logger.LogInformation(
            "[WhatsAppBot] Inbound webhook received. TenantId={TenantId} Provider={Provider} MsgId={ProviderMessageId} From={From} BodyPreview={BodyPreview}",
            request.TenantId,
            string.IsNullOrEmpty(request.Provider) ? "(empty)" : request.Provider,
            request.ProviderMessageId,
            request.From,
            bodyPreview);

        try
        {
            await _service.ProcessInboundMessageAsync(request, cancellationToken);
            _logger.LogInformation(
                "[WhatsAppBot] Inbound processing finished OK. TenantId={TenantId} MsgId={ProviderMessageId}",
                request.TenantId,
                request.ProviderMessageId);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing inbound WhatsApp message. TenantId: {TenantId}, ProviderMessageId: {ProviderMessageId}", request.TenantId, request.ProviderMessageId);
            return StatusCode(500, new { message = "An error occurred while processing inbound WhatsApp message." });
        }
    }
}
