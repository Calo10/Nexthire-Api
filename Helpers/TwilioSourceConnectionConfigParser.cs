using System.Text.Json;
using nexthire_api.Options;

namespace nexthire_api.Helpers;

public static class TwilioSourceConnectionConfigParser
{
    public static TwilioOrgCredentials Parse(string configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            throw new ArgumentException("Twilio config_json is empty.", nameof(configJson));

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Twilio config_json is not valid JSON.", nameof(configJson), ex);
        }

        var accountSid = ReadString(root,
            "Twilio:AccountSid",
            "twilio:AccountSid",
            "accountSid",
            "AccountSid");

        var authToken = ReadString(root,
            "Twilio:AuthToken",
            "twilio:AuthToken",
            "authToken",
            "AuthToken");

        var fromNumber = ReadString(root,
            "Twilio:DefaultFromWhatsAppNumber",
            "twilio:DefaultFromWhatsAppNumber",
            "defaultFromWhatsAppNumber",
            "DefaultFromWhatsAppNumber",
            "from",
            "From",
            "whatsappFrom");

        if (string.IsNullOrWhiteSpace(accountSid))
            throw new ArgumentException("Twilio config_json is missing AccountSid.");
        if (string.IsNullOrWhiteSpace(authToken))
            throw new ArgumentException("Twilio config_json is missing AuthToken.");
        if (string.IsNullOrWhiteSpace(fromNumber))
            throw new ArgumentException("Twilio config_json is missing DefaultFromWhatsAppNumber.");

        return new TwilioOrgCredentials
        {
            AccountSid = accountSid.Trim(),
            AuthToken = authToken.Trim(),
            DefaultFromWhatsAppNumber = fromNumber.Trim()
        };
    }

    private static string? ReadString(JsonElement root, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (!root.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
            {
                var s = value.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }
        }

        return null;
    }
}
