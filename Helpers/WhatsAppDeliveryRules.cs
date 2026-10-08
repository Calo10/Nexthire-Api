using System.Text.Json;

namespace nexthire_api.Helpers;

/// <summary>
/// Twilio message status for an outbound WhatsApp send. Terminal states stop further lookups.
/// </summary>
public static class WhatsAppDeliveryRules
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(20);

    public static bool IsTerminal(string? status)
    {
        return Normalize(status) is "delivered" or "undelivered" or "failed" or "read";
    }

    public static bool NeedsRefresh(
        string? direction,
        string? providerMessageId,
        string? status,
        DateTimeOffset? checkedAtUtc,
        DateTimeOffset utcNow)
    {
        if (!string.Equals(direction, "outbound", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!IsTwilioMessageSid(providerMessageId))
            return false;
        if (IsTerminal(status))
            return false;
        if (checkedAtUtc.HasValue && utcNow - checkedAtUtc.Value < RefreshInterval)
            return false;

        return true;
    }

    public static bool IsTwilioMessageSid(string? providerMessageId)
    {
        if (string.IsNullOrWhiteSpace(providerMessageId))
            return false;

        var sid = providerMessageId.Trim();
        return sid.StartsWith("SM", StringComparison.Ordinal) || sid.StartsWith("MM", StringComparison.Ordinal);
    }

    public static string? Normalize(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return null;

        return status.Trim().ToLowerInvariant();
    }

    public static bool TryReadTwilioStatus(string json, out string status, out string? errorCode)
    {
        status = string.Empty;
        errorCode = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("status", out var statusEl) || statusEl.ValueKind != JsonValueKind.String)
                return false;

            var normalized = Normalize(statusEl.GetString());
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            status = normalized;
            if (doc.RootElement.TryGetProperty("error_code", out var codeEl) && codeEl.ValueKind != JsonValueKind.Null)
            {
                errorCode = codeEl.ValueKind == JsonValueKind.Number
                    ? codeEl.GetRawText()
                    : codeEl.GetString();
                if (string.IsNullOrWhiteSpace(errorCode))
                    errorCode = null;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
