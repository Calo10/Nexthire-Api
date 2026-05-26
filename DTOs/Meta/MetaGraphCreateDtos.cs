using System.Text.Json;
using System.Text.Json.Serialization;

namespace nexthire_api.DTOs.Meta;

/// <summary>
/// Graph API fields for POST /{ad_account_id}/campaigns.
/// Property names serialize to snake_case. Use <see cref="ExtensionData"/> for any other Meta field (snake_case keys).
/// </summary>
public class MetaCampaignGraphPayload
{
    public string? Name { get; set; }
    public string? Objective { get; set; }
    public string? Status { get; set; }
    public string[]? SpecialAdCategories { get; set; }

    /// <summary>
    /// ISO 3166-1 alpha-2 codes (e.g. US). Required by Meta when <see cref="SpecialAdCategories"/> is non-empty.
    /// Must include every country where the ad set audience is located.
    /// </summary>
    public string[]? SpecialAdCategoryCountry { get; set; }

    public bool? IsAdsetBudgetSharingEnabled { get; set; }
    public string? BuyingType { get; set; }
    public string? BidStrategy { get; set; }
    public long? DailyBudget { get; set; }
    public long? LifetimeBudget { get; set; }
    public long? SpendCap { get; set; }
    public string? StartTime { get; set; }
    public string? StopTime { get; set; }
    public JsonElement? PromotedObject { get; set; }
    public string? SourceCampaignId { get; set; }
    public bool? IsSkadnetworkAttribution { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Graph API fields for POST /{ad_account_id}/adsets.</summary>
public class MetaAdSetGraphPayload
{
    public string? Name { get; set; }
    public string? Status { get; set; }
    public long? DailyBudget { get; set; }
    public long? LifetimeBudget { get; set; }
    public string? StartTime { get; set; }
    public string? EndTime { get; set; }
    public string? BillingEvent { get; set; }
    public string? OptimizationGoal { get; set; }
    public long? BidAmount { get; set; }
    public string? BidStrategy { get; set; }
    public MetaTargetingGraphPayload? Targeting { get; set; }
    public JsonElement? PromotedObject { get; set; }
    public string? DestinationType { get; set; }
    public JsonElement? AttributionSpec { get; set; }
    public string? PacingType { get; set; }
    public long? DailyMinSpendTarget { get; set; }
    public long? LifetimeMinSpendTarget { get; set; }
    public JsonElement? FrequencyControlSpecs { get; set; }
    public bool? IsDynamicCreative { get; set; }
    public string? DsaPayor { get; set; }
    public string? DsaBeneficiary { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Graph API <c>targeting</c> object on ad sets.</summary>
public class MetaTargetingGraphPayload
{
    public MetaGeoLocationsGraphPayload? GeoLocations { get; set; }
    public int? AgeMin { get; set; }
    public int? AgeMax { get; set; }
    public int[]? Genders { get; set; }
    public string[]? PublisherPlatforms { get; set; }
    public string[]? FacebookPositions { get; set; }
    public string[]? InstagramPositions { get; set; }
    public string[]? MessengerPositions { get; set; }
    public string[]? AudienceNetworkPositions { get; set; }
    public string[]? DevicePlatforms { get; set; }
    public string[]? UserOs { get; set; }
    public string[]? UserDevice { get; set; }
    public string[]? WirelessCarrier { get; set; }
    public int[]? Locales { get; set; }
    public JsonElement? FlexibleSpec { get; set; }
    public JsonElement? Exclusions { get; set; }
    public JsonElement? CustomAudiences { get; set; }
    public JsonElement? ExcludedCustomAudiences { get; set; }
    public MetaTargetingAutomationGraphPayload? TargetingAutomation { get; set; }
    public JsonElement? GeoLocationsExcluded { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class MetaGeoLocationsGraphPayload
{
    public string[]? Countries { get; set; }
    public JsonElement? Regions { get; set; }
    public JsonElement? Cities { get; set; }
    public JsonElement? Zips { get; set; }
    public JsonElement? GeoMarkets { get; set; }
    public JsonElement? CustomLocations { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class MetaTargetingAutomationGraphPayload
{
    public int? AdvantageAudience { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Graph API fields for POST /{ad_account_id}/adcreatives.</summary>
public class MetaAdCreativeGraphPayload
{
    public string? Name { get; set; }
    public MetaObjectStorySpecGraphPayload? ObjectStorySpec { get; set; }
    public JsonElement? AssetFeedSpec { get; set; }
    public JsonElement? DegreesOfFreedomSpec { get; set; }
    public string? UrlTags { get; set; }
    public JsonElement? PlatformCustomizations { get; set; }
    public JsonElement? TemplateUrlSpec { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class MetaObjectStorySpecGraphPayload
{
    public string? PageId { get; set; }
    public string? InstagramActorId { get; set; }
    public string? InstagramUserId { get; set; }
    public MetaLinkDataGraphPayload? LinkData { get; set; }
    public JsonElement? VideoData { get; set; }
    public JsonElement? TemplateData { get; set; }
    public JsonElement? PhotoData { get; set; }
    public JsonElement? TextData { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class MetaLinkDataGraphPayload
{
    public string? Message { get; set; }
    public string? Link { get; set; }
    public string? ImageHash { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Caption { get; set; }
    public MetaCallToActionGraphPayload? CallToAction { get; set; }
    public JsonElement? ChildAttachments { get; set; }
    public bool? MultiShareOptimized { get; set; }
    public string? Picture { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class MetaCallToActionGraphPayload
{
    public string? Type { get; set; }
    public JsonElement? Value { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Graph API fields for POST /{ad_account_id}/ads.</summary>
public class MetaAdGraphPayload
{
    public string? Name { get; set; }
    public string? Status { get; set; }
    public JsonElement? TrackingSpecs { get; set; }
    public string? ConversionDomain { get; set; }
    public int? DisplaySequence { get; set; }
    public int? Priority { get; set; }
    public JsonElement? Adlabels { get; set; }
    public JsonElement? Creative { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
