using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

[ApiController]
[Route("api/whatsapp/conversations")]
public class WhatsAppInboxController : ControllerBase
{
    private readonly IWhatsAppInboundService _service;
    private readonly ILogger<WhatsAppInboxController> _logger;

    public WhatsAppInboxController(
        IWhatsAppInboundService service,
        ILogger<WhatsAppInboxController> logger)
    {
        _service = service;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WhatsAppConversationListItemDto>>> GetConversations(
        [FromQuery] string tenantId,
        [FromQuery] string? status)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return BadRequest(new { message = "tenantId is required." });

        var conversations = await _service.GetConversationsAsync(tenantId.Trim(), status);
        return Ok(conversations);
    }

    /// <summary>
    /// WhatsApp conversation + full message history for a single candidate (tenant-scoped).
    /// </summary>
    [HttpGet("by-candidate")]
    public async Task<ActionResult<WhatsAppCandidateConversationDto>> GetConversationByCandidate(
        [FromQuery] string tenantId,
        [FromQuery] Guid candidateId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return BadRequest(new { message = "tenantId is required." });
        if (candidateId == Guid.Empty)
            return BadRequest(new { message = "candidateId is required." });

        var result = await _service.GetConversationByCandidateAsync(tenantId.Trim(), candidateId);
        if (result == null)
            return NotFound(new { message = "No WhatsApp conversation found for this candidate." });

        return Ok(result);
    }

    [HttpGet("{conversationId:guid}/messages")]
    public async Task<ActionResult<IReadOnlyList<WhatsAppConversationMessageDto>>> GetMessages(
        Guid conversationId,
        [FromQuery] string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return BadRequest(new { message = "tenantId is required." });

        var messages = await _service.GetConversationMessagesAsync(conversationId, tenantId.Trim());
        return Ok(messages);
    }

    [HttpPost("{conversationId:guid}/send")]
    public async Task<ActionResult<SendWhatsAppMessageResponseDto>> SendMessage(
        Guid conversationId,
        [FromBody] SendWhatsAppMessageRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);
        if (string.IsNullOrWhiteSpace(request.TenantId))
            return BadRequest(new { message = "tenantId is required." });
        if (string.IsNullOrWhiteSpace(request.Body))
            return BadRequest(new { message = "body is required." });

        try
        {
            var response = await _service.SendMessageAsync(conversationId, request, cancellationToken);
            return Ok(response);
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { message = "Conversation not found." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send WhatsApp message. ConversationId: {ConversationId}", conversationId);
            return StatusCode(500, new { message = "Failed to send WhatsApp message." });
        }
    }

    [HttpPost("send-direct")]
    public async Task<ActionResult<SendWhatsAppMessageResponseDto>> SendDirectMessage(
        [FromBody] SendDirectWhatsAppMessageRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);
        if (string.IsNullOrWhiteSpace(request.TenantId))
            return BadRequest(new { message = "tenantId is required." });
        if (string.IsNullOrWhiteSpace(request.To))
            return BadRequest(new { message = "to is required." });
        if (string.IsNullOrWhiteSpace(request.Body))
            return BadRequest(new { message = "body is required." });

        try
        {
            var response = await _service.SendDirectMessageAsync(request, cancellationToken);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send direct WhatsApp message. TenantId: {TenantId}, CandidateId: {CandidateId}", request.TenantId, request.CandidateId);
            return StatusCode(500, new { message = "Failed to send WhatsApp message." });
        }
    }

    [HttpPatch("{conversationId:guid}")]
    public async Task<IActionResult> UpdateConversation(
        Guid conversationId,
        [FromQuery] string tenantId,
        [FromBody] UpdateWhatsAppConversationRequestDto request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return BadRequest(new { message = "tenantId is required." });

        var updated = await _service.UpdateConversationAsync(conversationId, tenantId.Trim(), request, cancellationToken);
        if (!updated)
            return NotFound(new { message = "Conversation not found." });

        return Ok(new { updated = true });
    }
}
