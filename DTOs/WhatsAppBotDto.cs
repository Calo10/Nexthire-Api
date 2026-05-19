namespace nexthire_api.DTOs;

public class WhatsAppConversationContext
{
    public Guid ConversationId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string ContactPhone { get; set; } = string.Empty;
    public string BusinessPhone { get; set; } = string.Empty;
}

public class WhatsAppInboundSaveResult
{
    public WhatsAppConversationContext Context { get; set; } = null!;
    /// <summary>False when the same provider_message_id was already stored (webhook retry / duplicate delivery).</summary>
    public bool MessageInserted { get; set; }
}

public class WhatsAppBotConfigDto
{
    public string TenantId { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public string SystemPrompt { get; set; } = string.Empty;
    public int HistoryMessageLimit { get; set; } = 10;
    public string? BotModel { get; set; }
}

public class WhatsAppMessageHistoryItemDto
{
    public string Direction { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset ReceivedAtUtc { get; set; }
}
