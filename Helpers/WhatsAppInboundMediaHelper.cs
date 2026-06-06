using System.Text.Json;
using nexthire_api.DTOs;

namespace nexthire_api.Helpers;

/// <summary>
/// Normalizes Twilio-style inbound payloads (MediaUrl0, MediaContentType0) into <see cref="WhatsAppInboundMessageDto.Media"/>.
/// </summary>
public static class WhatsAppInboundMediaHelper
{
    public static void EnsureMediaPopulated(WhatsAppInboundMessageDto inbound)
    {
        if (inbound.Media is { Count: > 0 })
            return;

        if (inbound.RawProviderPayload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return;

        var root = inbound.RawProviderPayload;
        if (!TryGetInt(root, "NumMedia", out var numMedia) || numMedia <= 0)
            return;

        for (var i = 0; i < numMedia; i++)
        {
            var url = TryGetString(root, $"MediaUrl{i}");
            if (string.IsNullOrWhiteSpace(url))
                continue;

            inbound.Media.Add(new WhatsAppInboundMediaDto
            {
                Url = url.Trim(),
                MimeType = TryGetString(root, $"MediaContentType{i}")?.Trim()
            });
        }
    }

    public static bool HasInboundMedia(WhatsAppInboundMessageDto inbound)
    {
        EnsureMediaPopulated(inbound);
        return inbound.Media.Any(m => !string.IsNullOrWhiteSpace(m.Url));
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (root.TryGetProperty(propertyName, out var exact) && exact.ValueKind == JsonValueKind.String)
            return exact.GetString();

        foreach (var prop in root.EnumerateObject())
        {
            if (!prop.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            return prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
        }

        return null;
    }

    private static bool TryGetInt(JsonElement root, string propertyName, out int value)
    {
        value = 0;
        var raw = TryGetString(root, propertyName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (root.TryGetProperty(propertyName, out var exact) && exact.ValueKind == JsonValueKind.Number)
            {
                value = exact.GetInt32();
                return true;
            }

            foreach (var prop in root.EnumerateObject())
            {
                if (!prop.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (prop.Value.ValueKind == JsonValueKind.Number)
                {
                    value = prop.Value.GetInt32();
                    return true;
                }

                if (prop.Value.ValueKind == JsonValueKind.String && int.TryParse(prop.Value.GetString(), out value))
                    return true;
            }

            return false;
        }

        return int.TryParse(raw.Trim(), out value);
    }
}
