using nexthire_api.DTOs.AiAgents;

namespace nexthire_api.Agents;

/// <summary>
/// Mira (offer): offer sends and follow-ups for candidates in a pipeline stage.
/// Email uses message templates; WhatsApp uses org Twilio content SIDs.
/// </summary>
public static class MiraOfferPlaybook
{
    public const string AgentId = "offer";
    public const string ChannelEmail = "email";
    public const string ChannelWhatsApp = "whatsapp";
    public const string ActionOffer = "offer";
    public const string ActionFollowUp = "followup";
    public const string DefaultStageName = "Offer";
    public const int DefaultMaxProcessed = 10;
    public const int DefaultResponseWaitHours = 48;
    public const int TokensPerScan = 15;
    public const int TokensPerOffer = 120;
    public const int TokensPerFollowUp = 80;

    public sealed record Decision(
        Guid CandidateId,
        Guid ApplicationId,
        string CandidateName,
        string CandidateEmail,
        string JobTitle,
        string Action);

    public sealed record Sweep(
        IReadOnlyList<Decision> ToContact,
        int Eligible,
        int Skipped);

    public static string NormalizeChannel(string? channel)
    {
        var value = (channel ?? string.Empty).Trim().ToLowerInvariant();
        return value == ChannelWhatsApp ? ChannelWhatsApp : ChannelEmail;
    }

    public static int ClampMaxProcessed(int? value)
    {
        var n = value ?? DefaultMaxProcessed;
        return Math.Clamp(n, 1, 50);
    }

    public static int ClampResponseWaitHours(int? value)
    {
        var n = value ?? DefaultResponseWaitHours;
        return Math.Clamp(n, 1, 720);
    }

    public static Guid? ParseTemplateId(string? templateId)
    {
        var raw = (templateId ?? string.Empty).Trim();
        return Guid.TryParse(raw, out var id) && id != Guid.Empty ? id : null;
    }

    public static void PrepareContext(AgentContextDto incoming, AgentContextDto? existing)
    {
        AriaSourcingPlaybook.PrepareContext(incoming, existing);
        incoming.Channel = NormalizeChannel(incoming.Channel);
        // Prefer MaxOffersToSend as the per-run cap; keep MaxProcessed in sync for shared UI.
        var cap = incoming.MaxOffersToSend > 0 ? incoming.MaxOffersToSend : incoming.MaxProcessed;
        incoming.MaxOffersToSend = ClampMaxProcessed(cap);
        incoming.MaxProcessed = incoming.MaxOffersToSend;
        incoming.ResponseWaitHours = ClampResponseWaitHours(incoming.ResponseWaitHours);
        incoming.TemplateId = (incoming.TemplateId ?? string.Empty).Trim();

        if (existing is not null)
        {
            if ((incoming.Outreach is null || incoming.Outreach.Count == 0) && existing.Outreach is { Count: > 0 })
                incoming.Outreach = [.. existing.Outreach];
            if (incoming.StageId is null && existing.StageId is not null)
                incoming.StageId = existing.StageId;
            if (string.IsNullOrWhiteSpace(incoming.TemplateId) && !string.IsNullOrWhiteSpace(existing.TemplateId))
                incoming.TemplateId = existing.TemplateId;
        }

        incoming.Outreach ??= [];
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

    public static Sweep Plan(
        AgentContextDto context,
        IReadOnlyList<(Guid ApplicationId, Guid CandidateId, string CandidateName, string CandidateEmail, string JobTitle, string Status)> apps,
        DateTimeOffset now)
    {
        var channel = NormalizeChannel(context.Channel);
        var wait = TimeSpan.FromHours(ClampResponseWaitHours(context.ResponseWaitHours));
        var max = ClampMaxProcessed(context.MaxProcessed);
        var stamps = (context.Outreach ?? [])
            .Where(item => string.Equals(item.Channel, channel, StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.CandidateId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.At).First());

        var toContact = new List<Decision>();
        var skipped = 0;
        var eligible = 0;

        foreach (var app in apps)
        {
            if (string.Equals(app.Status, "archived", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(app.Status, "rejected", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(app.Status, "hired", StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            eligible++;
            if (toContact.Count >= max) continue;

            if (!stamps.TryGetValue(app.CandidateId, out var last))
            {
                toContact.Add(new Decision(
                    app.CandidateId,
                    app.ApplicationId,
                    app.CandidateName,
                    app.CandidateEmail,
                    app.JobTitle,
                    ActionOffer));
                continue;
            }

            if (now - last.At >= wait)
            {
                toContact.Add(new Decision(
                    app.CandidateId,
                    app.ApplicationId,
                    app.CandidateName,
                    app.CandidateEmail,
                    app.JobTitle,
                    ActionFollowUp));
            }
            else
            {
                skipped++;
            }
        }

        return new Sweep(toContact, eligible, skipped);
    }

    public static KaiOutreachDto Stamp(
        Guid candidateId,
        Guid? applicationId,
        string channel,
        string action,
        DateTimeOffset now) => new()
    {
        CandidateId = candidateId,
        ApplicationId = applicationId,
        Channel = NormalizeChannel(channel),
        Action = action == ActionFollowUp ? ActionFollowUp : ActionOffer,
        At = now,
    };

    public static void RememberOutreach(AgentContextDto context, IEnumerable<KaiOutreachDto> updates)
    {
        var map = (context.Outreach ?? [])
            .ToDictionary(item => $"{item.Channel}:{item.CandidateId}", StringComparer.OrdinalIgnoreCase);
        foreach (var update in updates)
            map[$"{update.Channel}:{update.CandidateId}"] = update;

        context.Outreach = map.Values
            .OrderByDescending(item => item.At)
            .Take(500)
            .ToList();
    }

    public static string RenderTemplate(string? template, string candidateName, string jobTitle, string? notes)
    {
        var body = template ?? string.Empty;
        var first = candidateName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? candidateName;
        return body
            .Replace("{{name}}", candidateName, StringComparison.OrdinalIgnoreCase)
            .Replace("{{candidate_name}}", candidateName, StringComparison.OrdinalIgnoreCase)
            .Replace("{{first_name}}", first, StringComparison.OrdinalIgnoreCase)
            .Replace("{{job}}", jobTitle, StringComparison.OrdinalIgnoreCase)
            .Replace("{{job_title}}", jobTitle, StringComparison.OrdinalIgnoreCase)
            .Replace("{{notes}}", notes ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    public static AriaReviewDto RecordReview(
        AgentContextDto context,
        MiraRunResult result,
        long tokens,
        DateTimeOffset now)
    {
        var key = result.Offers > 0 || result.FollowUps > 0
            ? "offerOutreach"
            : result.Skipped > 0
                ? "offerSkipped"
                : "offerEmpty";

        var review = new AriaReviewDto
        {
            Key = key,
            Scanned = result.Eligible,
            Scored = result.Offers,
            Converted = result.Offers + result.FollowUps,
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

        context.ConvertedCandidates = Math.Max(0, context.ConvertedCandidates) + result.Offers + result.FollowUps;
        context.LastSummary = review.Summary ?? context.LastSummary;
        context.LastPlan = review.Key;
        return review;
    }

    public static string BuildSummary(MiraRunResult result) =>
        $"Eligible {result.Eligible}, offers {result.Offers}, follow-ups {result.FollowUps} " +
        $"(skipped {result.Skipped}, errors {result.Errors}).";

    public static long EstimateTokens(MiraRunResult result) =>
        (result.Eligible * (long)TokensPerScan)
        + (result.Offers * (long)TokensPerOffer)
        + (result.FollowUps * (long)TokensPerFollowUp);
}

public sealed class MiraRunResult
{
    public int Eligible { get; set; }
    public int Planned { get; set; }
    public int Offers { get; set; }
    public int FollowUps { get; set; }
    public int Skipped { get; set; }
    public int Errors { get; set; }
}
