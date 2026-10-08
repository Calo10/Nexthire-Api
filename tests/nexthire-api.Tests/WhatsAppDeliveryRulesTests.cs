using FluentAssertions;
using nexthire_api.Helpers;
using Xunit;

namespace nexthire_api.Tests;

public class WhatsAppDeliveryRulesTests
{
    [Fact]
    public void Undelivered_marketing_status_is_read_from_twilio()
    {
        var json = """{"status":"undelivered","error_code":63049}""";

        WhatsAppDeliveryRules.TryReadTwilioStatus(json, out var status, out var errorCode).Should().BeTrue();
        status.Should().Be("undelivered");
        errorCode.Should().Be("63049");
        WhatsAppDeliveryRules.IsTerminal(status).Should().BeTrue();
    }

    [Fact]
    public void Sent_messages_are_refreshed_until_twilio_reports_a_terminal_status()
    {
        var now = DateTimeOffset.Parse("2026-10-08T04:00:00Z");

        WhatsAppDeliveryRules.NeedsRefresh("outbound", "SMabc", "sent", now.AddSeconds(-30), now).Should().BeTrue();
        WhatsAppDeliveryRules.NeedsRefresh("outbound", "SMabc", "sent", now.AddSeconds(-5), now).Should().BeFalse();
        WhatsAppDeliveryRules.NeedsRefresh("outbound", "SMabc", "undelivered", null, now).Should().BeFalse();
        WhatsAppDeliveryRules.NeedsRefresh("inbound", "SMabc", null, null, now).Should().BeFalse();
    }
}
