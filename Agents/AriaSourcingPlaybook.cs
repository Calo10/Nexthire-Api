using nexthire_api.DTOs.AiAgents;
using nexthire_api.DTOs.Sourcing;

namespace nexthire_api.Agents;

/// <summary>
/// Aria (sourcing): process inbox leads, score fit when missing, convert the top % into candidates.
/// </summary>
public static class AriaSourcingPlaybook
{
    public const string ManualCadence = "manual";
    public const int DefaultMaxProcessed = 100;
    public const int DefaultAutoConversionPercent = 10;
    public const int MinFitScoreToConvert = 50;
    public const int TokensPerScore = 800;
    public const int TokensPerConvert = 40;
    public const int TokensPerScan = 15;

    private static readonly HashSet<string> Cadences = new(StringComparer.OrdinalIgnoreCase)
    {
        ManualCadence,
        "hourly",
        "daily",
        "weekly",
    };

    private static readonly string[] ProcessableStatuses = ["new", "qualified"];

    public static string NormalizeCadence(string? value)
    {
        var cadence = (value ?? string.Empty).Trim().ToLowerInvariant();
        return Cadences.Contains(cadence) ? cadence : "daily";
    }

    public static TimeSpan? CadencePeriod(string? cadence) => NormalizeCadence(cadence) switch
    {
        "hourly" => TimeSpan.FromHours(1),
        "daily" => TimeSpan.FromDays(1),
        "weekly" => TimeSpan.FromDays(7),
        _ => null,
    };

    public static int ClampMaxProcessed(int? value)
    {
        var n = value ?? DefaultMaxProcessed;
        return Math.Clamp(n, 1, 200);
    }

    public static int ClampAutoConversionPercent(int? value)
    {
        var n = value ?? DefaultAutoConversionPercent;
        return Math.Clamp(n, 0, 100);
    }

    public static IReadOnlyList<string> ProcessableLeadStatuses => ProcessableStatuses;

    public static bool IsDue(AgentContextDto context, DateTimeOffset now)
    {
        if (NormalizeCadence(context.Cadence) == ManualCadence) return false;
        if (context.NextRunAt is null) return true;
        return context.NextRunAt.Value <= now;
    }

    public static void StampRun(AgentContextDto context, DateTimeOffset now)
    {
        context.LastRunAt = now;
        var period = CadencePeriod(context.Cadence);
        context.NextRunAt = period is null ? null : now + period.Value;
    }

    public static void PrepareSchedule(AgentContextDto incoming, AgentContextDto? existing, DateTimeOffset now)
    {
        PrepareContext(incoming, existing);
        var cadence = NormalizeCadence(incoming.Cadence);
        incoming.Cadence = cadence;

        if (cadence == ManualCadence)
        {
            incoming.NextRunAt = null;
            return;
        }

        var period = CadencePeriod(cadence);
        if (period is null)
        {
            incoming.NextRunAt = null;
            return;
        }

        var cadenceChanged = existing is null
            || !string.Equals(NormalizeCadence(existing.Cadence), cadence, StringComparison.OrdinalIgnoreCase);

        if (cadenceChanged || incoming.NextRunAt is null)
            incoming.NextRunAt = now + period.Value;
    }

    public static void PrepareContext(AgentContextDto incoming, AgentContextDto? existing)
    {
        incoming.Cadence = NormalizeCadence(incoming.Cadence);
        incoming.MaxProcessed = ClampMaxProcessed(incoming.MaxProcessed);
        incoming.AutoConversionPercent = ClampAutoConversionPercent(incoming.AutoConversionPercent);

        if (existing is not null)
        {
            incoming.LastRunAt ??= existing.LastRunAt;
            incoming.NextRunAt ??= existing.NextRunAt;
            if (string.IsNullOrWhiteSpace(incoming.LastSummary))
                incoming.LastSummary = existing.LastSummary;
            if (string.IsNullOrWhiteSpace(incoming.LastPlan))
                incoming.LastPlan = existing.LastPlan;
            if (incoming.ChatTokens <= 0 && existing.ChatTokens > 0)
                incoming.ChatTokens = existing.ChatTokens;
            if (incoming.ConvertedCandidates <= 0 && existing.ConvertedCandidates > 0)
                incoming.ConvertedCandidates = existing.ConvertedCandidates;
            if ((incoming.Reviews is null || incoming.Reviews.Count == 0) && existing.Reviews is { Count: > 0 })
                incoming.Reviews = [.. existing.Reviews];
        }

        incoming.Reviews ??= [];
    }

    public static AriaReviewDto RecordReview(AgentContextDto context, AriaRunResult result, long tokens, DateTimeOffset now)
    {
        var review = new AriaReviewDto
        {
            Key = "score+convert",
            Scanned = result.Scanned,
            Scored = result.Scored,
            Converted = result.Converted,
            Quota = result.Quota,
            Skipped = result.Skipped,
            Errors = result.Errors,
            Tokens = Math.Max(0, tokens),
            Next = NormalizeCadence(context.Cadence) == ManualCadence ? "manual" : "cadence",
            Summary = BuildSummary(result),
            At = now,
        };

        context.Reviews ??= [];
        context.Reviews.Insert(0, review);
        if (context.Reviews.Count > 14)
            context.Reviews = context.Reviews.Take(14).ToList();

        context.ConvertedCandidates = Math.Max(0, context.ConvertedCandidates) + Math.Max(0, result.Converted);
        context.LastSummary = review.Summary ?? context.LastSummary;
        context.LastPlan = review.Key;
        return review;
    }

    public static int ConversionQuota(int processedCount, int autoConversionPercent)
    {
        if (processedCount <= 0 || autoConversionPercent <= 0) return 0;
        return Math.Max(1, (int)Math.Ceiling(processedCount * (autoConversionPercent / 100.0)));
    }

    public static List<SourcingLeadListItemDto> SelectForConversion(
        IReadOnlyList<SourcingLeadListItemDto> leads,
        int quota)
    {
        if (quota <= 0 || leads.Count == 0) return [];

        var eligible = leads
            .Where(lead => !string.Equals(lead.Status, "converted", StringComparison.OrdinalIgnoreCase))
            .Where(lead => !string.Equals(lead.Status, "rejected", StringComparison.OrdinalIgnoreCase))
            .Where(lead => !string.Equals(lead.Status, "archived", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var strong = eligible
            .Where(lead => lead.FitScore.HasValue && lead.FitScore.Value >= MinFitScoreToConvert)
            .OrderByDescending(lead => lead.FitScore ?? 0)
            .ThenBy(lead => lead.CreatedAt)
            .Take(quota)
            .ToList();

        if (strong.Count > 0)
            return strong;

        // Fit scoring off / no scores yet: convert oldest eligible leads up to quota.
        return eligible
            .OrderBy(lead => lead.CreatedAt)
            .Take(quota)
            .ToList();
    }

    public static string BuildSummary(AriaRunResult result)
    {
        return
            $"Scanned {result.Scanned}, scored {result.Scored}, converted {result.Converted} " +
            $"(quota {result.Quota}, skipped {result.Skipped}, errors {result.Errors}).";
    }

    public static long EstimateTokens(AriaRunResult result) =>
        (result.Scanned * TokensPerScan)
        + (result.Scored * (long)TokensPerScore)
        + (result.Converted * (long)TokensPerConvert);
}

public sealed class AriaRunResult
{
    public int Scanned { get; set; }
    public int Scored { get; set; }
    public int Converted { get; set; }
    public int Quota { get; set; }
    public int Skipped { get; set; }
    public int Errors { get; set; }
    public List<Guid> ConvertedLeadIds { get; } = [];
    public List<Guid> ConvertedCandidateIds { get; } = [];
}
