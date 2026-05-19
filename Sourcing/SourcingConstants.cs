namespace nexthire_api.Sourcing;

public static class SourcingConstants
{
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public static readonly string[] LeadStatuses =
    {
        "new", "contacted", "qualified", "converted", "rejected", "archived"
    };

    public static readonly string[] CampaignStatuses =
    {
        "draft", "active", "paused", "completed", "archived"
    };

    public static readonly string[] TrackingEventTypes =
    {
        "page_view", "apply_started", "apply_submitted", "whatsapp_clicked", "qr_scanned"
    };

    public const string DefaultLeadStatus = "new";
    public const string DefaultCampaignStatus = "draft";
    public const string DefaultCurrency = "USD";
}
