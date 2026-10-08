namespace nexthire_api.Options;

/// <summary>Twilio credentials resolved per organization from sourcing_source_connections.</summary>
public sealed class TwilioOrgCredentials
{
    public string AccountSid { get; init; } = string.Empty;
    public string AuthToken { get; init; } = string.Empty;
    public string DefaultFromWhatsAppNumber { get; init; } = string.Empty;

    /// <summary>
    /// Twilio Content SID for this organization's WhatsApp account.
    /// Optional for freeform sends. Required for introduction template sends.
    /// </summary>
    public string? DefaultWhatsAppContentSid { get; init; }

    /// <summary>
    /// Second Twilio Content SID, used only to reopen a conversation after the 24-hour window.
    /// </summary>
    public string? FollowUpWhatsAppContentSid { get; init; }
}
