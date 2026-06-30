using System.Text.Json;
using System.Text.RegularExpressions;

namespace nexthire_api.Helpers;

/// <summary>
/// On update, the UI often resends masked secrets; keep stored values when placeholders are detected.
/// </summary>
public static partial class TwilioSourceConnectionConfigMerger
{
    private static readonly string[] AuthTokenKeys =
    {
        "Twilio:AuthToken",
        "twilio:AuthToken",
        "authToken",
        "AuthToken"
    };

    public static string Merge(string? incomingJson, string? existingJson)
    {
        if (string.IsNullOrWhiteSpace(incomingJson))
            return existingJson ?? string.Empty;
        if (string.IsNullOrWhiteSpace(existingJson))
            return incomingJson;

        using var incomingDoc = JsonDocument.Parse(incomingJson);
        using var existingDoc = JsonDocument.Parse(existingJson);

        var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in incomingDoc.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
                merged[property.Name] = property.Value.GetString();
        }

        foreach (var key in AuthTokenKeys)
        {
            if (!merged.TryGetValue(key, out var incomingToken) || !IsMaskedOrPlaceholder(incomingToken))
                continue;

            var existingToken = ReadString(existingDoc.RootElement, AuthTokenKeys);
            if (!string.IsNullOrWhiteSpace(existingToken))
                merged[key] = existingToken;
        }

        return JsonSerializer.Serialize(merged);
    }

    private static string? ReadString(JsonElement root, IEnumerable<string> propertyNames)
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

    private static bool IsMaskedOrPlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var trimmed = value.Trim();
        if (trimmed is "********" or "••••••••" or "************")
            return true;

        if (MaskedTokenPattern().IsMatch(trimmed))
            return true;

        return trimmed.All(c => c is '*' or '•' or '·' or '.');
    }

    [GeneratedRegex(@"^[\*•·\.]+$")]
    private static partial Regex MaskedTokenPattern();
}
