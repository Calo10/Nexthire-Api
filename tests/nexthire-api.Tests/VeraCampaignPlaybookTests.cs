using FluentAssertions;
using nexthire_api.Agents;
using nexthire_api.DTOs.AiAgents;
using Xunit;

namespace nexthire_api.Tests;

public class VeraCampaignPlaybookTests
{
    [Fact]
    public void NormalizePlatforms_maps_both_and_csv()
    {
        VeraCampaignPlaybook.NormalizePlatforms("both").Should().Equal("meta", "linkedin");
        VeraCampaignPlaybook.NormalizePlatforms("meta").Should().Equal("meta");
        VeraCampaignPlaybook.NormalizePlatforms("linkedin,meta").Should().Equal("linkedin", "meta");
    }

    [Fact]
    public void Plan_creates_when_no_stamp_and_manages_when_present()
    {
        var jobId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var context = new AgentContextDto
        {
            JobId = jobId,
            Channel = "meta",
            Campaigns =
            [
                new VeraCampaignDto
                {
                    CampaignId = campaignId,
                    JobId = jobId,
                    Platform = "meta",
                    Name = "Existing",
                    Status = "paused",
                    Action = "created",
                    At = DateTimeOffset.UtcNow.AddDays(-1),
                },
            ],
        };

        var manage = VeraCampaignPlaybook.Plan(context);
        manage.ToManage.Should().ContainSingle();
        manage.ToCreate.Should().BeEmpty();

        context.Campaigns = [];
        var create = VeraCampaignPlaybook.Plan(context);
        create.ToCreate.Should().ContainSingle(item => item.Platform == "meta");
    }

    [Fact]
    public void RecordReview_tracks_created_and_managed()
    {
        var context = new AgentContextDto { Cadence = "daily", ConvertedCandidates = 1 };
        var result = new VeraRunResult { Planned = 2, Created = 1, Managed = 1, Skipped = 0, Errors = 0 };
        var now = DateTimeOffset.Parse("2026-11-20T18:00:00Z");

        var review = VeraCampaignPlaybook.RecordReview(context, result, tokens: 1500, now);

        review.Key.Should().Be("campaignLaunched");
        context.ConvertedCandidates.Should().Be(3);
        context.Reviews.Should().HaveCount(1);
        context.LastSummary.Should().Contain("created 1");
    }

    [Fact]
    public void VeraChatInterpreter_parses_budget_patch()
    {
        var json =
            """{"reply":"Raising budget and launching.","action":"update_and_run","context":{"campaignDailyBudget":25,"campaignDesiredStatus":"active"}}""";
        var decision = VeraChatInterpreter.Parse(json);
        decision.Action.Should().Be("update_and_run");

        var context = new AgentContextDto { CampaignDailyBudget = 10, CampaignDesiredStatus = "paused" };
        VeraChatInterpreter.ApplyPatch(context, decision.Context);
        context.CampaignDailyBudget.Should().Be(25);
        context.CampaignDesiredStatus.Should().Be("active");
    }

    [Fact]
    public void ClampDailyBudget_bounds_values()
    {
        VeraCampaignPlaybook.ClampDailyBudget(0).Should().Be(1);
        VeraCampaignPlaybook.ClampDailyBudget(999).Should().Be(500);
        VeraCampaignPlaybook.ClampDailyBudget(12.345m).Should().Be(12.34m);
    }
}
