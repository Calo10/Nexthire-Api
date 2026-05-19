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
    public string? MimeType { get; set; }
    public string? Caption { get; set; }
}
