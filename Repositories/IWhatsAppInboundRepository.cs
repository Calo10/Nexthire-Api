using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public interface IWhatsAppInboundRepository
{
    Task<WhatsAppInboundSaveResult> SaveInboundMessageAsync(
        WhatsAppInboundMessageDto inbound,
        string normalizedPhoneNumber,
        CancellationToken cancellationToken);

    Task<WhatsAppBotConfigDto?> GetBotConfigAsync(string tenantId);

    Task<IReadOnlyList<WhatsAppMessageHistoryItemDto>> GetRecentMessagesAsync(Guid conversationId, int maxMessages);

    Task SaveOutboundMessageAsync(
        Guid conversationId,
        string tenantId,
        string provider,
        string fromPhone,
        string toPhone,
        string body,
        string? providerMessageId,
        CancellationToken cancellationToken);

    Task MarkNeedsHumanAsync(Guid conversationId, CancellationToken cancellationToken);

    Task<IReadOnlyList<WhatsAppConversationListItemDto>> GetConversationsAsync(string tenantId, string? status);

    Task<WhatsAppConversationListItemDto?> GetConversationByCandidateAsync(string tenantId, Guid candidateId);

    Task<IReadOnlyList<WhatsAppConversationMessageDto>> GetConversationMessagesAsync(Guid conversationId, string tenantId);

    Task<WhatsAppConversationSendContextDto?> GetConversationSendContextAsync(Guid conversationId, string tenantId);

    Task<WhatsAppConversationSendContextDto> EnsureConversationForDirectSendAsync(
        string tenantId,
        string toPhone,
        Guid? candidateId,
        CancellationToken cancellationToken);

    Task<Guid> SaveOutboundMessageForInboxAsync(
        Guid conversationId,
        string tenantId,
        string provider,
        string? fromPhone,
        string toPhone,
        string body,
        string? providerMessageId,
        CancellationToken cancellationToken);

    Task<bool> UpdateConversationAsync(
        Guid conversationId,
        string tenantId,
        UpdateWhatsAppConversationRequestDto request,
        CancellationToken cancellationToken);

    Task<WhatsAppConversationApplyStateDto?> GetConversationApplyStateAsync(
        Guid conversationId,
        string tenantId,
        CancellationToken cancellationToken);

    Task UpdateConversationApplyStateAsync(
        Guid conversationId,
        string tenantId,
        Guid? jobId,
        string? botApplySessionJson,
        CancellationToken cancellationToken);
}
