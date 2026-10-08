using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs;
using nexthire_api.Exceptions;
using nexthire_api.Services;
using nexthire_api.Security;

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

    [HttpPost("{conversationId:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid conversationId, [FromQuery] string tenantId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return BadRequest(new { message = "tenantId is required." });

        var updated = await _service.MarkConversationReadAsync(conversationId, tenantId.Trim(), cancellationToken);
        if (!updated)
            return NotFound(new { message = "Conversation not found." });

        return Ok(new { read = true });
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
        catch (WhatsAppCustomerCareWindowException ex)
        {
            return Conflict(new { message = ex.Message });
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
        catch (WhatsAppCustomerCareWindowException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send direct WhatsApp message. TenantId: {TenantId}, CandidateId: {CandidateId}", request.TenantId, request.CandidateId);
            return StatusCode(500, new { message = "Failed to send WhatsApp message." });
        }
    }

    [HttpPost("send-introduction")]
    [Authorize]
    public async Task<ActionResult<SendWhatsAppIntroductionResponseDto>> SendIntroduction(
        [FromBody] SendWhatsAppIntroductionRequestDto request,
        CancellationToken cancellationToken)
    {
        if (request == null || request.CandidateId == Guid.Empty)
            return BadRequest(new { message = "candidateId is required." });

        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var nexaUserId = ClaimUtils.RequireNexaUserId(User);
            var response = await _service.SendIntroductionAsync(orgId, nexaUserId, request.CandidateId, cancellationToken);
            return Ok(response);
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized(new { message = "Missing or invalid organization or user identity." });
        }
        catch (WhatsAppIntroductionException ex)
        {
            _logger.LogWarning(
                "WhatsApp introduction rejected. Status={Status} Reason={Reason}",
                ex.StatusCode,
                ex.Message);
            return StatusCode(ex.StatusCode, new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WhatsApp introduction failed. CandidateId={CandidateId}", request.CandidateId);
            return StatusCode(500, new { message = "Failed to send WhatsApp introduction." });
        }
    }

    [HttpPost("send-follow-up")]
    [Authorize]
    public async Task<ActionResult<SendWhatsAppIntroductionResponseDto>> SendFollowUp(
        [FromBody] SendWhatsAppIntroductionRequestDto request,
        CancellationToken cancellationToken)
    {
        if (request == null || request.CandidateId == Guid.Empty)
            return BadRequest(new { message = "candidateId is required." });

        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var nexaUserId = ClaimUtils.RequireNexaUserId(User);
            var response = await _service.SendFollowUpAsync(orgId, nexaUserId, request.CandidateId, cancellationToken);
            return Ok(response);
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized(new { message = "Missing or invalid organization or user identity." });
        }
        catch (WhatsAppIntroductionException ex)
        {
            _logger.LogWarning(
                "WhatsApp follow-up rejected. Status={Status} Reason={Reason}",
                ex.StatusCode,
                ex.Message);
            return StatusCode(ex.StatusCode, new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WhatsApp follow-up failed. CandidateId={CandidateId}", request.CandidateId);
            return StatusCode(500, new { message = "Failed to send WhatsApp follow-up." });
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
