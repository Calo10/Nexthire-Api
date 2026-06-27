namespace nexthire_api.Helpers;

/// <summary>
/// Inbound webhook is the same for every org; messenger routes by <c>tenantId</c>.
/// </summary>
public static class WhatsAppInboundWebhookUrlResolver
{
    public const string InboundPath = "/api/integrations/whatsapp/inbound";

    public static string Resolve(IConfiguration? configuration = null)
    {
        var configured = configuration?["MessengerFunction:InboundWebhookUrl"]?.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var hostname = Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME")?.Trim();
        if (!string.IsNullOrWhiteSpace(hostname))
            return $"https://{hostname}{InboundPath}";

        return $"http://localhost:5000{InboundPath}";
    }
}
