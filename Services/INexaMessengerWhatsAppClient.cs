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

    Task<string?> SendTemplateMessageAsync(
        string tenantId,
        string toPhone,
        string contentSid,
        IReadOnlyDictionary<string, string> contentVariables,
        TwilioOrgCredentials twilio,
        CancellationToken cancellationToken = default);
}
