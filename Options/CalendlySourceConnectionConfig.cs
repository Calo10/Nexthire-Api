namespace nexthire_api.Options;

/// <summary>Shape of <c>sourcing_source_connections.config_json</c> for Calendly.</summary>
public class CalendlySourceConnectionConfig
{
    /// <summary>Calendly Personal Access Token (Bearer).</summary>
    public string PersonalAccessToken { get; set; } = string.Empty;

    /// <summary>Optional public scheduling URL (e.g. https://calendly.com/you/30min). Filled from /users/me when empty.</summary>
    public string? SchedulingUrl { get; set; }

    /// <summary>Optional webhook signing key for future invitee.created webhooks.</summary>
    public string? WebhookSigningKey { get; set; }
}
