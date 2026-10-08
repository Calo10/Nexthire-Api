using System.Reflection;
using FluentAssertions;
using nexthire_api.DTOs;
using Xunit;
using nexthire_api.Exceptions;
using nexthire_api.Helpers;

namespace nexthire_api.Tests;

public class WhatsAppIntroductionTests
{
    [Fact]
    public void Existing_twilio_config_without_content_sid_still_parses()
    {
        const string json = """
            {
              "Twilio:AccountSid": "AC123",
              "Twilio:AuthToken": "token",
              "Twilio:DefaultFromWhatsAppNumber": "+14155238886"
            }
            """;

        var credentials = TwilioSourceConnectionConfigParser.Parse(json);

        credentials.AccountSid.Should().Be("AC123");
        credentials.AuthToken.Should().Be("token");
        credentials.DefaultFromWhatsAppNumber.Should().Be("+14155238886");
        credentials.DefaultWhatsAppContentSid.Should().BeNull();
    }

    [Fact]
    public void Content_sid_is_read_from_the_tenant_twilio_connection()
    {
        const string json = """
            {
              "Twilio:AccountSid": "AC123",
              "Twilio:AuthToken": "token",
              "Twilio:DefaultFromWhatsAppNumber": "+14155238886",
              "Twilio:DefaultWhatsAppContentSid": "HX123"
            }
            """;

        var credentials = TwilioSourceConnectionConfigParser.Parse(json);

        credentials.DefaultWhatsAppContentSid.Should().Be("HX123");
        credentials.FollowUpWhatsAppContentSid.Should().BeNull();
    }

    [Fact]
    public void Follow_up_content_sid_is_read_separately()
    {
        const string json = """
            {
              "Twilio:AccountSid": "AC123",
              "Twilio:AuthToken": "token",
              "Twilio:DefaultFromWhatsAppNumber": "+14155238886",
              "Twilio:DefaultWhatsAppContentSid": "HX123",
              "Twilio:FollowUpWhatsAppContentSid": "HX999"
            }
            """;

        var credentials = TwilioSourceConnectionConfigParser.Parse(json);

        credentials.DefaultWhatsAppContentSid.Should().Be("HX123");
        credentials.FollowUpWhatsAppContentSid.Should().Be("HX999");
    }

    [Fact]
    public void Missing_content_sid_fails_with_a_configuration_error()
    {
        var act = () => WhatsAppIntroductionRules.RequireContentSid("  ");

        act.Should().Throw<WhatsAppIntroductionException>()
            .WithMessage("*DefaultWhatsAppContentSid*");
    }

    [Fact]
    public void Foreign_organization_is_rejected()
    {
        var tenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var tenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var act = () => WhatsAppIntroductionRules.RejectForeignOrganization(tenantA, tenantB);

        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Same_organization_is_allowed()
    {
        var orgId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var act = () => WhatsAppIntroductionRules.RejectForeignOrganization(orgId, orgId);

        act.Should().NotThrow();
    }

    [Fact]
    public void Introduction_request_accepts_only_the_candidate_id()
    {
        var names = typeof(SendWhatsAppIntroductionRequestDto)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();

        names.Should().Equal("CandidateId");
    }

    [Fact]
    public void Email_local_part_is_not_used_as_a_person_name()
    {
        WhatsAppIntroductionRules.IsAccountHandle("yendry.fonseca", "yendry.fonseca@gmail.com").Should().BeTrue();
        WhatsAppIntroductionRules.PersonNameOrNull("yendry.fonseca", "yendry.fonseca@gmail.com").Should().BeNull();
        WhatsAppIntroductionRules.PersonNameOrNull("Yendry Fonseca", "yendry.fonseca@gmail.com").Should().Be("Yendry Fonseca");
    }

    [Fact]
    public void Variables_greet_the_candidate_and_introduce_the_recruiter()
    {
        var variables = WhatsAppIntroductionRules.BuildVariables("Yendry Fonseca", "Linda Rease", "EC Corp");

        variables.Should().Equal(new Dictionary<string, string>
        {
            ["1"] = "Linda Rease",
            ["2"] = "Yendry Fonseca",
            ["3"] = "EC Corp"
        });

        WhatsAppIntroductionHistoryText.Format("Yendry Fonseca", "Linda Rease", "EC Corp")
            .Should().StartWith("Hola Linda Rease, soy Yendry Fonseca de EC Corp.");
    }

    [Fact]
    public void Missing_template_variable_is_rejected()
    {
        var act = () => WhatsAppIntroductionRules.BuildVariables("Yendry Fonseca", "Carlos Mendez", " ");

        act.Should().Throw<WhatsAppIntroductionException>()
            .WithMessage("*Organization name*");
    }

    [Fact]
    public void History_text_includes_the_resolved_names_and_is_not_a_content_sid()
    {
        var text = WhatsAppIntroductionHistoryText.Format("Yendry Fonseca", "Carlos Mendez", "NCA Hospitality");

        text.Should().Contain("Carlos Mendez");
        text.Should().Contain("Yendry Fonseca");
        text.Should().Contain("NCA Hospitality");
        text.Should().NotContain("HX");
    }
}
