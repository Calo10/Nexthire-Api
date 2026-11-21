using nexthire_api.DTOs.AiAgents;

namespace nexthire_api.Agents;

/// <summary>
/// Vera (campaign): create and sync Meta hiring campaigns tied to a job.
/// LinkedIn is accepted in context for future runs but skipped until ads exist in NextHire.
/// </summary>
public static class VeraCampaignPlaybook
{
    public const string AgentId = "campaign";
    public const string PlatformMeta = "meta";
    public const string PlatformLinkedIn = "linkedin";
    public const string StatusActive = "active";
    public const string StatusPaused = "paused";
    public const string ActionCreated = "created";
    public const string ActionSynced = "synced";
    public const string ActionSkipped = "skipped";
    public const decimal DefaultDailyBudget = 10m;
    public const decimal MinDailyBudget = 1m;
    public const decimal MaxDailyBudget = 500m;
    public const int TokensPerCreate = 1200;
    public const int TokensPerSync = 80;
    public const int TokensPerScan = 20;

    public sealed record WorkItem(string Platform, Guid? ExistingCampaignId);

    public sealed record Sweep(
        IReadOnlyList<WorkItem> ToCreate,
        IReadOnlyList<WorkItem> ToManage,
        int Tracked);

    public static List<string> NormalizePlatforms(string? channelOrCsv)
    {
        var raw = (channelOrCsv ?? string.Empty).Trim().ToLowerInvariant();
        if (raw is "both" or "meta+linkedin" or "all")
            return [PlatformMeta, PlatformLinkedIn];
        if (raw.Contains(','))
        {
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => item.ToLowerInvariant())
                .Where(item => item is PlatformMeta or PlatformLinkedIn)
                .Distinct()
                .DefaultIfEmpty(PlatformMeta)
                .ToList();
        }

        if (raw == PlatformLinkedIn) return [PlatformLinkedIn];
        return [PlatformMeta];
    }

    public static string NormalizeDesiredStatus(string? status)
    {
        var value = (status ?? string.Empty).Trim().ToLowerInvariant();
        return value == StatusActive ? StatusActive : StatusPaused;
    }

    public static decimal ClampDailyBudget(decimal? budget)
    {
        var value = budget ?? DefaultDailyBudget;
        if (value < MinDailyBudget) return MinDailyBudget;
        if (value > MaxDailyBudget) return MaxDailyBudget;
        return Math.Round(value, 2);
    }

    public static string NormalizeCountryCode(string? country)
    {
        var value = (country ?? string.Empty).Trim().ToUpperInvariant();
        if (value.Length == 2 && value.All(char.IsLetter)) return value;
        return "US";
    }

    public static string NormalizeDestinationUrl(string? url)
    {
        var value = (url ?? string.Empty).Trim();
        if (value.Length == 0) return string.Empty;
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = "https://" + value;
        }

        return value.Length > 2000 ? value[..2000] : value;
    }

    public static void PrepareContext(AgentContextDto incoming, AgentContextDto? existing)
    {
        AriaSourcingPlaybook.PrepareContext(incoming, existing);
        incoming.Channel = string.IsNullOrWhiteSpace(incoming.Channel) ? PlatformMeta : incoming.Channel.Trim().ToLowerInvariant();
        incoming.CampaignDailyBudget = ClampDailyBudget(incoming.CampaignDailyBudget);
        incoming.CampaignDesiredStatus = NormalizeDesiredStatus(incoming.CampaignDesiredStatus);
        incoming.CampaignCountryCode = NormalizeCountryCode(incoming.CampaignCountryCode);
        incoming.CampaignDestinationUrl = NormalizeDestinationUrl(incoming.CampaignDestinationUrl);

        if (existing is not null)
        {
            if ((incoming.Campaigns is null || incoming.Campaigns.Count == 0) && existing.Campaigns is { Count: > 0 })
                incoming.Campaigns = [.. existing.Campaigns];
            if (incoming.CampaignDailyBudget <= 0 && existing.CampaignDailyBudget > 0)
                incoming.CampaignDailyBudget = existing.CampaignDailyBudget;
            if (string.IsNullOrWhiteSpace(incoming.CampaignDestinationUrl) && !string.IsNullOrWhiteSpace(existing.CampaignDestinationUrl))
                incoming.CampaignDestinationUrl = existing.CampaignDestinationUrl;
        }

        // Legacy UI stored budget in maxProcessed — migrate once.
        if (incoming.CampaignDailyBudget <= 0 && incoming.MaxProcessed is > 0 and <= 500)
            incoming.CampaignDailyBudget = ClampDailyBudget(incoming.MaxProcessed);

        incoming.Campaigns ??= [];
    }

    public static void PrepareSchedule(AgentContextDto incoming, AgentContextDto? existing, DateTimeOffset now)
    {
        PrepareContext(incoming, existing);
        var cadence = AriaSourcingPlaybook.NormalizeCadence(incoming.Cadence);
        incoming.Cadence = cadence;

        if (cadence == AriaSourcingPlaybook.ManualCadence)
        {
            incoming.NextRunAt = null;
            return;
        }

        var period = AriaSourcingPlaybook.CadencePeriod(cadence);
        if (period is null)
        {
            incoming.NextRunAt = null;
            return;
        }

        var cadenceChanged = existing is null
            || !string.Equals(
                AriaSourcingPlaybook.NormalizeCadence(existing.Cadence),
                cadence,
                StringComparison.OrdinalIgnoreCase);

        if (cadenceChanged || incoming.NextRunAt is null)
            incoming.NextRunAt = now + period.Value;
    }

    public static Sweep Plan(AgentContextDto context)
    {
        var platforms = NormalizePlatforms(context.Channel);
        var jobId = context.JobId;
        var stamped = (context.Campaigns ?? [])
            .Where(item => jobId is null || item.JobId == jobId)
            .ToList();
        var byPlatform = stamped
            .GroupBy(item => (item.Platform ?? string.Empty).Trim().ToLowerInvariant())
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.At).First(),
                StringComparer.OrdinalIgnoreCase);

        var toCreate = new List<WorkItem>();
        var toManage = new List<WorkItem>();

        foreach (var platform in platforms)
        {
            if (byPlatform.TryGetValue(platform, out var existing) && existing.CampaignId != Guid.Empty)
                toManage.Add(new WorkItem(platform, existing.CampaignId));
            else
                toCreate.Add(new WorkItem(platform, null));
        }

        return new Sweep(toCreate, toManage, stamped.Count);
    }

    public static VeraCampaignDto Stamp(
        string platform,
        Guid campaignId,
        Guid? jobId,
        string name,
        string status,
        string action,
        DateTimeOffset now) => new()
    {
        CampaignId = campaignId,
        JobId = jobId,
        Platform = platform,
        Name = Truncate(name, 240),
        Status = NormalizeDesiredStatus(status),
        Action = action,
        At = now,
    };

    public static void RememberCampaigns(AgentContextDto context, IEnumerable<VeraCampaignDto> updates)
    {
        var map = (context.Campaigns ?? [])
            .ToDictionary(item => $"{item.Platform}:{item.JobId}:{item.CampaignId}", StringComparer.OrdinalIgnoreCase);
        foreach (var update in updates)
        {
            var key = $"{update.Platform}:{update.JobId}:{update.CampaignId}";
            map[key] = update;
        }

        context.Campaigns = map.Values
            .OrderByDescending(item => item.At)
            .Take(200)
            .ToList();
    }

    public static string CampaignName(string jobTitle, string platform, DateTimeOffset now)
    {
        var baseName = string.IsNullOrWhiteSpace(jobTitle) ? "NextHire" : jobTitle.Trim();
        var label = platform == PlatformLinkedIn ? "LinkedIn" : "Meta";
        var stamp = now.ToString("yyyy-MM-dd");
        var name = $"{baseName} · {label} · {stamp}";
        return name.Length <= 200 ? name : name[..200];
    }

    public static string Headline(AgentContextDto context, string jobTitle)
    {
        if (!string.IsNullOrWhiteSpace(context.Notes))
        {
            var line = context.Notes.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(line)) return Truncate(line!, 200);
        }

        if (!string.IsNullOrWhiteSpace(jobTitle)) return Truncate($"Hiring: {jobTitle.Trim()}", 200);
        return "We're hiring — apply today";
    }

    public static string IntroText(AgentContextDto context, string? jobDescription)
    {
        var parts = new[]
        {
            context.Notes,
            jobDescription,
        }.Where(item => !string.IsNullOrWhiteSpace(item));
        var text = string.Join(" ", parts).Trim();
        if (text.Length == 0)
            text = "Join our team. Apply now and take the next step in your career.";
        return Truncate(text, 3000);
    }

    public static AriaReviewDto RecordReview(
        AgentContextDto context,
        VeraRunResult result,
        long tokens,
        DateTimeOffset now)
    {
        var key = result.Created > 0
            ? "campaignLaunched"
            : result.Managed > 0
                ? "campaignSynced"
                : result.Skipped > 0
                    ? "campaignSkipped"
                    : "campaignEmpty";

        var review = new AriaReviewDto
        {
            Key = key,
            Scanned = result.Planned,
            Scored = result.Created,
            Converted = result.Managed,
            Quota = result.Planned,
            Skipped = result.Skipped,
            Errors = result.Errors,
            Tokens = Math.Max(0, tokens),
            Next = AriaSourcingPlaybook.NormalizeCadence(context.Cadence) == AriaSourcingPlaybook.ManualCadence
                ? "manual"
                : "cadence",
            Summary = BuildSummary(result),
            At = now,
        };

        context.Reviews ??= [];
        context.Reviews.Insert(0, review);
        if (context.Reviews.Count > 14)
            context.Reviews = context.Reviews.Take(14).ToList();

        context.ConvertedCandidates = Math.Max(0, context.ConvertedCandidates) + result.Created + result.Managed;
        context.LastSummary = review.Summary ?? context.LastSummary;
        context.LastPlan = review.Key;
        return review;
    }

    public static string BuildSummary(VeraRunResult result) =>
        $"Planned {result.Planned}, created {result.Created}, synced {result.Managed} " +
        $"(skipped {result.Skipped}, errors {result.Errors}).";

    public static long EstimateTokens(VeraRunResult result) =>
        (result.Planned * TokensPerScan)
        + (result.Created * (long)TokensPerCreate)
        + (result.Managed * (long)TokensPerSync);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}

public sealed class VeraRunResult
{
    public int Planned { get; set; }
    public int Created { get; set; }
    public int Managed { get; set; }
    public int Skipped { get; set; }
    public int Errors { get; set; }
}
