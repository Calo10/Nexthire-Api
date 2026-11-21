using FluentAssertions;
using nexthire_api.Agents;
using nexthire_api.DTOs.AiAgents;
using nexthire_api.DTOs.Sourcing;
using Xunit;

namespace nexthire_api.Tests;

public class AriaSourcingPlaybookTests
{
    [Fact]
    public void IsDue_is_false_for_manual_cadence()
    {
        var context = new AgentContextDto { Cadence = "manual", NextRunAt = DateTimeOffset.UtcNow.AddHours(-1) };
        AriaSourcingPlaybook.IsDue(context, DateTimeOffset.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void IsDue_is_true_when_next_run_is_past()
    {
        var now = DateTimeOffset.Parse("2026-11-20T18:00:00Z");
        var context = new AgentContextDto { Cadence = "daily", NextRunAt = now.AddMinutes(-1) };
        AriaSourcingPlaybook.IsDue(context, now).Should().BeTrue();
    }

    [Fact]
    public void PrepareSchedule_sets_next_run_when_cadence_becomes_daily()
    {
        var now = DateTimeOffset.Parse("2026-11-20T18:00:00Z");
        var incoming = new AgentContextDto { Cadence = "daily", MaxProcessed = 50, AutoConversionPercent = 10 };
        var existing = new AgentContextDto { Cadence = "manual", MaxProcessed = 50, AutoConversionPercent = 10 };

        AriaSourcingPlaybook.PrepareSchedule(incoming, existing, now);

        incoming.Cadence.Should().Be("daily");
        incoming.NextRunAt.Should().Be(now.AddDays(1));
    }

    [Fact]
    public void SelectForConversion_prefers_high_fit_scores()
    {
        var leads = new List<SourcingLeadListItemDto>
        {
            new() { Id = Guid.NewGuid(), Status = "new", FitScore = 40, CreatedAt = DateTimeOffset.UtcNow.AddDays(-2) },
            new() { Id = Guid.NewGuid(), Status = "new", FitScore = 90, CreatedAt = DateTimeOffset.UtcNow.AddDays(-1) },
            new() { Id = Guid.NewGuid(), Status = "new", FitScore = 80, CreatedAt = DateTimeOffset.UtcNow },
        };

        var selected = AriaSourcingPlaybook.SelectForConversion(leads, quota: 1);

        selected.Should().HaveCount(1);
        selected[0].FitScore.Should().Be(90);
    }

    [Fact]
    public void RecordReview_prepends_and_tracks_converted_total()
    {
        var context = new AgentContextDto { Cadence = "daily", ConvertedCandidates = 2 };
        var result = new AriaRunResult { Scanned = 10, Scored = 4, Converted = 3, Quota = 3, Skipped = 7 };
        var now = DateTimeOffset.Parse("2026-11-20T18:00:00Z");

        var review = AriaSourcingPlaybook.RecordReview(context, result, tokens: 1200, now);

        review.Converted.Should().Be(3);
        context.Reviews.Should().HaveCount(1);
        context.Reviews[0].At.Should().Be(now);
        context.ConvertedCandidates.Should().Be(5);
        context.LastSummary.Should().Contain("converted 3");
        context.NextRunAt.Should().BeNull(); // StampRun not called here
    }

    [Fact]
    public void ConversionQuota_ceils_percent()
    {
        AriaSourcingPlaybook.ConversionQuota(10, 10).Should().Be(1);
        AriaSourcingPlaybook.ConversionQuota(10, 25).Should().Be(3);
        AriaSourcingPlaybook.ConversionQuota(0, 10).Should().Be(0);
    }

    [Fact]
    public void AriaChatInterpreter_parses_update_and_run()
    {
        var json = """{"reply":"Raising conversion and running.","action":"update_and_run","context":{"autoConversionPercent":20}}""";
        var decision = AriaChatInterpreter.Parse(json);

        decision.Action.Should().Be("update_and_run");
        AriaChatInterpreter.WantsRun(decision.Action).Should().BeTrue();
        AriaChatInterpreter.WantsUpdate(decision.Action).Should().BeTrue();

        var context = new AgentContextDto { AutoConversionPercent = 10 };
        AriaChatInterpreter.ApplyPatch(context, decision.Context);
        context.AutoConversionPercent.Should().Be(20);
    }
}
