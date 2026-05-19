using nexthire_api.DTOs;

namespace nexthire_api.Services;

public interface IWhatsAppInboundService
{
    Task ProcessInboundMessageAsync(WhatsAppInboundMessageDto inbound, CancellationToken cancellationToken);

    Task<IReadOnlyList<WhatsAppConversationListItemDto>> GetConversationsAsync(string tenantId, string? status);

    Task<WhatsAppCandidateConversationDto?> GetConversationByCandidateAsync(string tenantId, Guid candidateId);

    Task<IReadOnlyList<WhatsAppConversationMessageDto>> GetConversationMessagesAsync(Guid conversationId, string tenantId);

    Task<SendWhatsAppMessageResponseDto> SendMessageAsync(
        Guid conversationId,
        SendWhatsAppMessageRequestDto request,
        CancellationToken cancellationToken);

    Task<SendWhatsAppMessageResponseDto> SendDirectMessageAsync(
        SendDirectWhatsAppMessageRequestDto request,
        CancellationToken cancellationToken);

    Task<bool> UpdateConversationAsync(
        Guid conversationId,
        string tenantId,
        UpdateWhatsAppConversationRequestDto request,
        CancellationToken cancellationToken);
}
