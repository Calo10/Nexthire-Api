using FluentAssertions;
using nexthire_api.Agents;
using nexthire_api.DTOs.AiAgents;
using Xunit;

namespace nexthire_api.Tests;

public class KaiScreeningPlaybookTests
{
    [Fact]
    public void NormalizeChannel_defaults_to_email()
    {
        KaiScreeningPlaybook.NormalizeChannel("whatsapp").Should().Be("whatsapp");
        KaiScreeningPlaybook.NormalizeChannel("EMAIL").Should().Be("email");
        KaiScreeningPlaybook.NormalizeChannel(null).Should().Be("email");
    }

    [Fact]
    public void Plan_schedules_intro_then_followup_after_wait()
    {
        var candidateId = Guid.NewGuid();
        var applicationId = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-11-21T12:00:00Z");
        var context = new AgentContextDto
        {
            Channel = "email",
            ResponseWaitHours = 48,
            MaxProcessed = 10,
        };

        var apps = new[]
        {
            (applicationId, candidateId, "Ada Lovelace", "ada@example.com", "Engineer", "active"),
        };

        var first = KaiScreeningPlaybook.Plan(context, apps, now);
        first.ToContact.Should().ContainSingle(item => item.Action == KaiScreeningPlaybook.ActionIntro);

        KaiScreeningPlaybook.RememberOutreach(context, [
            KaiScreeningPlaybook.Stamp(candidateId, applicationId, "email", KaiScreeningPlaybook.ActionIntro, now.AddHours(-49)),
        ]);

        var second = KaiScreeningPlaybook.Plan(context, apps, now);
        second.ToContact.Should().ContainSingle(item => item.Action == KaiScreeningPlaybook.ActionFollowUp);

        KaiScreeningPlaybook.RememberOutreach(context, [
            KaiScreeningPlaybook.Stamp(candidateId, applicationId, "email", KaiScreeningPlaybook.ActionIntro, now.AddHours(-1)),
        ]);
        var early = KaiScreeningPlaybook.Plan(context, apps, now);
        early.ToContact.Should().BeEmpty();
        early.Skipped.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RecordReview_tracks_outreach_totals()
    {
        var context = new AgentContextDto { Cadence = "daily", ConvertedCandidates = 2 };
        var result = new KaiRunResult { Eligible = 5, Planned = 3, Intros = 2, FollowUps = 1, Skipped = 2, Errors = 0 };
        var now = DateTimeOffset.Parse("2026-11-21T12:00:00Z");

        var review = KaiScreeningPlaybook.RecordReview(context, result, tokens: 400, now);

        review.Key.Should().Be("screeningOutreach");
        review.Converted.Should().Be(3);
        context.ConvertedCandidates.Should().Be(5);
        context.LastSummary.Should().Contain("intros 2");
    }

    [Fact]
    public void KaiChatInterpreter_parses_wait_and_channel_patch()
    {
        var json =
            """{"reply":"Switching to WhatsApp and waiting 24h.","action":"update_and_run","context":{"channel":"whatsapp","responseWaitHours":24,"maxProcessed":15}}""";
        var decision = KaiChatInterpreter.Parse(json);
        decision.Action.Should().Be("update_and_run");

        var context = new AgentContextDto { Channel = "email", ResponseWaitHours = 48, MaxProcessed = 25 };
        KaiChatInterpreter.ApplyPatch(context, decision.Context);
        context.Channel.Should().Be("whatsapp");
        context.ResponseWaitHours.Should().Be(24);
        context.MaxProcessed.Should().Be(15);
    }

    [Fact]
    public void RenderTemplate_replaces_placeholders()
    {
        var body = KaiScreeningPlaybook.RenderTemplate(
            "Hi {{first_name}}, apply for {{job_title}}.",
            "Ada Lovelace",
            "Engineer",
            notes: null);
        body.Should().Be("Hi Ada, apply for Engineer.");
    }
}
