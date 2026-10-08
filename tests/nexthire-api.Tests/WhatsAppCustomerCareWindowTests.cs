using FluentAssertions;
using nexthire_api.Helpers;
using Xunit;

namespace nexthire_api.Tests;

public class WhatsAppCustomerCareWindowTests
{
    [Fact]
    public void Freeform_is_open_only_for_24_hours_after_the_last_inbound()
    {
        var now = DateTimeOffset.Parse("2026-10-08T04:00:00Z");

        WhatsAppCustomerCareWindow.IsOpen(now.AddHours(-23), now).Should().BeTrue();
        WhatsAppCustomerCareWindow.IsOpen(now.AddHours(-24), now).Should().BeFalse();
        WhatsAppCustomerCareWindow.IsOpen(null, now).Should().BeFalse();
    }
}
