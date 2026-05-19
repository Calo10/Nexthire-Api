using System.ComponentModel.DataAnnotations;

namespace nexthire_api.DTOs;

public class WhatsAppConversationListItemDto
{
    public Guid Id { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public string? ProfileName { get; set; }
    public Guid? CandidateId { get; set; }
    public Guid? JobId { get; set; }
    public Guid? ApplicationId { get; set; }
    public Guid? AssignedRecruiterId { get; set; }
    public string? Status { get; set; }
    public bool BotEnabled { get; set; }
    public DateTimeOffset? LastMessageAtUtc { get; set; }
}

public class WhatsAppConversationMessageDto
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public string Direction { get; set; } = string.Empty;
    public string? ProviderMessageId { get; set; }
    public string? FromPhone { get; set; }
    public string? ToPhone { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public class SendWhatsAppMessageRequestDto
{
    [Required]
    public string TenantId { get; set; } = string.Empty;

    [Required]
    public string Body { get; set; } = string.Empty;
}

public class SendWhatsAppMessageResponseDto
{
    public Guid MessageId { get; set; }
    public string Status { get; set; } = "sent";
}

public class SendDirectWhatsAppMessageRequestDto
{
    [Required]
    public string TenantId { get; set; } = string.Empty;

    public Guid? CandidateId { get; set; }

    [Required]
    public string To { get; set; } = string.Empty;

    [Required]
    public string Body { get; set; } = string.Empty;
}

public class UpdateWhatsAppConversationRequestDto
{
    public Guid? CandidateId { get; set; }
    public Guid? JobId { get; set; }
    public Guid? ApplicationId { get; set; }
    public Guid? AssignedRecruiterId { get; set; }
    public string? Status { get; set; }
    public bool? BotEnabled { get; set; }
}

public class WhatsAppConversationSendContextDto
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string? BusinessPhoneNumber { get; set; }
}

/// <summary>
/// Full WhatsApp thread for a candidate (conversation header + all messages).
/// </summary>
public class WhatsAppCandidateConversationDto
{
    public WhatsAppConversationListItemDto Conversation { get; set; } = null!;
    public IReadOnlyList<WhatsAppConversationMessageDto> Messages { get; set; } = Array.Empty<WhatsAppConversationMessageDto>();
}
