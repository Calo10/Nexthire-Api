using nexthire_api.Options;

namespace nexthire_api.Services;

public interface INexaMessengerWhatsAppRoutesClient
{
    Task RegisterRouteAsync(
        Guid orgId,
        TwilioOrgCredentials twilio,
        string inboundWebhookUrl,
        CancellationToken cancellationToken = default);

    Task<bool> RouteExistsAsync(
        Guid orgId,
        CancellationToken cancellationToken = default);
}
