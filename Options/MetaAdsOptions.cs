namespace nexthire_api.Options;

public class MetaAdsOptions
{
    public const string SectionName = "MetaAds";

    /// <summary>Long-lived user or system user access token (never log).</summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>Ad account id, with or without act_ prefix.</summary>
    public string AdAccountId { get; set; } = string.Empty;

    public string PageId { get; set; } = string.Empty;

    /// <summary>Graph API version, e.g. v23.0</summary>
    public string ApiVersion { get; set; } = "v23.0";
}
