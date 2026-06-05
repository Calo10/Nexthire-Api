using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace nexthire_api.DTOs;

public class WhatsAppInboundMessageDto
{
    [Required]
    public string TenantId { get; set; } = string.Empty;

    public string Channel { get; set; } = "whatsapp";

    public string Provider { get; set; } = string.Empty;

    [Required]
    public string ProviderMessageId { get; set; } = string.Empty;

    [Required]
    public string From { get; set; } = string.Empty;

    public string To { get; set; } = string.Empty;

    [Required]
    public string Body { get; set; } = string.Empty;

    public string? ProfileName { get; set; }

    public List<WhatsAppInboundMediaDto> Media { get; set; } = new();

    public DateTimeOffset ReceivedAtUtc { get; set; }

    public JsonElement RawProviderPayload { get; set; }
}

public class WhatsAppInboundMediaDto
{
    public string? Url { get; set; }

    /// <summary>From nexthire-api payloads.</summary>
    public string? MimeType { get; set; }

    /// <summary>From nexa-messenger forward payloads.</summary>
    public string? ContentType { get; set; }

    public string? ResolvedMimeType =>
        !string.IsNullOrWhiteSpace(MimeType) ? MimeType.Trim()
        : !string.IsNullOrWhiteSpace(ContentType) ? ContentType.Trim()
        : null;

    public string? Caption { get; set; }

    /// <summary>Optional file name when the messenger forwards downloaded bytes.</summary>
    public string? FileName { get; set; }

    /// <summary>Base64-encoded file bytes (avoids Twilio download from nexthire-api).</summary>
    public string? ContentBase64 { get; set; }
}
