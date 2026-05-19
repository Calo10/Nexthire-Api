namespace nexthire_api.Services;

public interface INexaMessengerWhatsAppClient
{
    Task<string?> SendMessageAsync(
        string tenantId,
        string toPhone,
        string body,
        CancellationToken cancellationToken = default);
}
