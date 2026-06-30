using System.Net.Http.Json;
using System.Text.Json;
using nexthire_api.Helpers;
using nexthire_api.Options;

namespace nexthire_api.Services;

public class NexaMessengerWhatsAppClient : INexaMessengerWhatsAppClient
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NexaMessengerWhatsAppClient> _logger;

    public NexaMessengerWhatsAppClient(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<NexaMessengerWhatsAppClient> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string?> SendMessageAsync(
        string tenantId,
        string toPhone,
        string body,
        TwilioOrgCredentials twilio,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = _configuration["MessengerFunction:WhatsAppUrl"]
            ?? _configuration["MessengerFunction:Url"];
        var functionCode = _configuration["MessengerFunction:Code"];

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(functionCode))
            throw new InvalidOperationException("MessengerFunction WhatsApp configuration is missing.");

        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("tenantId is required.", nameof(tenantId));

        var from = WhatsAppPhoneNormalizer.ToWhatsappPrefixed(twilio.DefaultFromWhatsAppNumber);
        if (string.IsNullOrWhiteSpace(from))
            throw new InvalidOperationException("Twilio DefaultFromWhatsAppNumber is not configured for this organization.");

        var requestUrl = $"{baseUrl}?code={Uri.EscapeDataString(functionCode)}";
        var payload = new
        {
            tenantId = tenantId.Trim(),
            channel = "whatsapp",
            to = toPhone,
            body,
            from,
            twilio = new
            {
                accountSid = twilio.AccountSid,
                authToken = twilio.AuthToken,
                from = from
            }
        };

        _logger.LogInformation(
            "Sending WhatsApp via Messenger Function. Url: {Url}, TenantId: {TenantId}, To: {To}, From: {From}, BodyLength: {BodyLength}, AccountSid: {AccountSid}",
            baseUrl,
            tenantId,
            toPhone,
            from,
            body?.Length ?? 0,
            twilio.AccountSid);

        var response = await _httpClient.PostAsJsonAsync(requestUrl, payload, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Messenger WhatsApp send failed. Url: {Url}, TenantId: {TenantId}, To: {To}, Status: {Status}, Response: {Response}",
                baseUrl, tenantId, toPhone, (int)response.StatusCode, Truncate(responseBody, 500));
            throw new InvalidOperationException($"WhatsApp send failed with status {(int)response.StatusCode}");
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("providerMessageId", out var idElement) &&
                idElement.ValueKind == JsonValueKind.String)
            {
                return idElement.GetString();
            }
        }
        catch
        {
            // Ignore parse issues and return null provider ID.
        }

        return null;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;
        return value[..maxLength];
    }
}
