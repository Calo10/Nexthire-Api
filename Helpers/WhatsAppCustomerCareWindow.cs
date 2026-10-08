namespace nexthire_api.Helpers;

/// <summary>
/// WhatsApp allows a freeform reply for 24 hours after the candidate's last inbound message.
/// Outside that window only an approved template is delivered.
/// </summary>
public static class WhatsAppCustomerCareWindow
{
    public static readonly TimeSpan Duration = TimeSpan.FromHours(24);

    public const string ClosedMessage =
        "The 24-hour WhatsApp window is closed. Send the template instead of a freeform message.";

    public static bool IsOpen(DateTimeOffset? lastInboundAtUtc, DateTimeOffset utcNow)
    {
        if (!lastInboundAtUtc.HasValue)
            return false;

        return utcNow - lastInboundAtUtc.Value < Duration;
    }
}
