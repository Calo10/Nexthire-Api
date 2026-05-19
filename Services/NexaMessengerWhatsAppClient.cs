using System.Net.Http.Json;
using System.Text.Json;

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
        CancellationToken cancellationToken = default)
    {
        var baseUrl = _configuration["MessengerFunction:WhatsAppUrl"]
            ?? _configuration["MessengerFunction:Url"];
        var functionCode = _configuration["MessengerFunction:Code"];
        var configuredTenantId = _configuration["MessengerFunction:TenantId"];
        var fromPhone = _configuration["MessengerFunction:WhatsAppFrom"];

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(functionCode))
            throw new InvalidOperationException("MessengerFunction WhatsApp configuration is missing.");

        var effectiveTenantId = string.IsNullOrWhiteSpace(configuredTenantId)
            ? tenantId
            : configuredTenantId;
        var requestUrl = $"{baseUrl}?code={Uri.EscapeDataString(functionCode)}";
        var payload = new
        {
            tenantId = effectiveTenantId,
            channel = "whatsapp",
            to = toPhone,
            body,
            from = fromPhone
        };

        _logger.LogInformation(
            "Sending WhatsApp via Messenger Function. Url: {Url}, TenantId: {TenantId}, To: {To}, BodyLength: {BodyLength}",
            baseUrl,
            effectiveTenantId,
            toPhone,
            body?.Length ?? 0);

        var response = await _httpClient.PostAsJsonAsync(requestUrl, payload, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Messenger WhatsApp send failed. Url: {Url}, TenantId: {TenantId}, To: {To}, Status: {Status}, Response: {Response}",
                baseUrl, effectiveTenantId, toPhone, (int)response.StatusCode, Truncate(responseBody, 500));
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
