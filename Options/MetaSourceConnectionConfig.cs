namespace nexthire_api.Options;

/// <summary>Shape of <c>sourcing_source_connections.config_json</c> for Meta Ads.</summary>
public class MetaSourceConnectionConfig
{
    public string AccessToken { get; set; } = string.Empty;

    public string AdAccountId { get; set; } = string.Empty;

    public string PageId { get; set; } = string.Empty;

    /// <summary>E.164-ish digits only or with + prefix. Used to build wa.me when the creative omits link.</summary>
    public string? WhatsappPhoneNumber { get; set; }
}
