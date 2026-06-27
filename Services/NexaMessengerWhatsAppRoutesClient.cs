using System.Net.Http.Json;
using nexthire_api.Helpers;
using nexthire_api.Options;

namespace nexthire_api.Services;

public class NexaMessengerWhatsAppRoutesClient : INexaMessengerWhatsAppRoutesClient
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NexaMessengerWhatsAppRoutesClient> _logger;

    public NexaMessengerWhatsAppRoutesClient(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<NexaMessengerWhatsAppRoutesClient> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task RegisterRouteAsync(
        Guid orgId,
        TwilioOrgCredentials twilio,
        string inboundWebhookUrl,
        CancellationToken cancellationToken = default)
    {
        var routesUrl = MessengerFunctionUrlHelper.GetWhatsAppRoutesUrl(_configuration);
        var functionCode = _configuration["MessengerFunction:Code"];

        if (string.IsNullOrWhiteSpace(functionCode))
            throw new InvalidOperationException("MessengerFunction Code is not configured.");

        var providerWhatsAppNumber = WhatsAppPhoneNormalizer.NormalizeForConversation(twilio.DefaultFromWhatsAppNumber);
        if (string.IsNullOrWhiteSpace(providerWhatsAppNumber))
            throw new InvalidOperationException("Twilio DefaultFromWhatsAppNumber is not configured.");

        var twilioFrom = WhatsAppPhoneNormalizer.ToWhatsappPrefixed(providerWhatsAppNumber);
        var tenantId = orgId.ToString();

        var payload = new
        {
            providerWhatsAppNumber,
            tenantId,
            inboundWebhookUrl,
            twilio = new
            {
                accountSid = twilio.AccountSid,
                authToken = twilio.AuthToken,
                from = twilioFrom
            }
        };

        _logger.LogInformation(
            "Registering WhatsApp inbound route in nexa-messenger. TenantId: {TenantId}, ProviderNumber: {ProviderNumber}, InboundWebhookUrl: {InboundWebhookUrl}, AccountSid: {AccountSid}",
            tenantId,
            providerWhatsAppNumber,
            inboundWebhookUrl,
            twilio.AccountSid);

        using var request = new HttpRequestMessage(HttpMethod.Post, routesUrl);
        request.Headers.TryAddWithoutValidation("x-functions-key", functionCode);
        request.Content = JsonContent.Create(payload);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "nexa-messenger WhatsApp route registration failed. TenantId: {TenantId}, Status: {Status}, Response: {Response}",
                tenantId,
                (int)response.StatusCode,
                Truncate(responseBody, 500));
            throw new InvalidOperationException(
                $"WhatsApp route registration failed with status {(int)response.StatusCode}.");
        }

        _logger.LogInformation(
            "WhatsApp inbound route registered in nexa-messenger. TenantId: {TenantId}, ProviderNumber: {ProviderNumber}",
            tenantId,
            providerWhatsAppNumber);
    }

    public async Task<bool> RouteExistsAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var routesUrl = MessengerFunctionUrlHelper.GetWhatsAppRoutesUrl(_configuration);
        var functionCode = _configuration["MessengerFunction:Code"];

        if (string.IsNullOrWhiteSpace(functionCode))
            throw new InvalidOperationException("MessengerFunction Code is not configured.");

        var tenantId = orgId.ToString();
        var requestUrl = $"{routesUrl}?tenantId={Uri.EscapeDataString(tenantId)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        request.Headers.TryAddWithoutValidation("x-functions-key", functionCode);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (responseBody.Contains("Message not found", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "nexa-messenger GET /routes returned 404 'Message not found' — likely route conflict with GET /messages/whatsapp/{{messageId}}. " +
                    "Deploy nexa-messenger with HttpGetWhatsAppRoutesFunction or use table WhatsAppProviderRouting to verify POST upsert.");
            }

            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "nexa-messenger WhatsApp route lookup failed. TenantId: {TenantId}, Status: {Status}, Response: {Response}",
                tenantId,
                (int)response.StatusCode,
                Truncate(responseBody, 500));
            return false;
        }

        return true;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;
        return value[..maxLength];
    }
}
