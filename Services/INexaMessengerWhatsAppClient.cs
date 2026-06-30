using nexthire_api.Options;

namespace nexthire_api.Services;

public interface INexaMessengerWhatsAppClient
{
    Task<string?> SendMessageAsync(
        string tenantId,
        string toPhone,
        string body,
        TwilioOrgCredentials twilio,
        CancellationToken cancellationToken = default);
}
