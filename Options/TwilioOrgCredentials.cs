namespace nexthire_api.Options;

/// <summary>Twilio credentials resolved per organization from sourcing_source_connections.</summary>
public sealed class TwilioOrgCredentials
{
    public string AccountSid { get; init; } = string.Empty;
    public string AuthToken { get; init; } = string.Empty;
    public string DefaultFromWhatsAppNumber { get; init; } = string.Empty;
}
