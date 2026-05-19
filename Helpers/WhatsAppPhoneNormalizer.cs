namespace nexthire_api.Helpers;

/// <summary>
/// Single canonical form for matching/storing WhatsApp participant numbers (E.164 with +).
/// Twilio often uses a "whatsapp:+..." prefix; we strip it for conversation keys.
/// </summary>
public static class WhatsAppPhoneNormalizer
{
    public static string NormalizeForConversation(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var s = raw.Trim();
        if (s.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase))
            s = s["whatsapp:".Length..].Trim();

        if (string.IsNullOrEmpty(s))
            return string.Empty;

        if (!s.StartsWith('+'))
            s = "+" + s.TrimStart('+');

        return s;
    }

    /// <summary>Twilio-style address: whatsapp:+15551234567</summary>
    public static string ToWhatsappPrefixed(string canonical)
    {
        var c = NormalizeForConversation(canonical);
        if (string.IsNullOrEmpty(c))
            return string.Empty;
        if (c.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase))
            return c;
        return "whatsapp:" + c;
    }
}
