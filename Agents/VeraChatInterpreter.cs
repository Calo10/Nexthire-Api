using System.Text;
using System.Text.Json;
using nexthire_api.DTOs.AiAgents;

namespace nexthire_api.Agents;

public static class VeraChatInterpreter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string SystemPrompt(string language) =>
        "You are Vera, the NextHire campaign specialist. You create and sync Meta hiring campaigns for open jobs. " +
        "Answer from the provided memory, last run summary, stamped campaigns, and this org's saved conversation. " +
        "If the user asks what you did last, summarize LAST RUN and RECENT CAMPAIGNS. " +
        "LinkedIn ads are not live in NextHire yet — say so if asked. " +
        $"Reply in {language}. Return JSON only with keys: reply, action, context. " +
        "action is one of: none, run_now, update_context, update_and_run. " +
        "Use update_context when the user changes job, budget, platforms, destination URL, desired status, cadence, or notes. " +
        "Use run_now when they want a create/sync pass now. Use update_and_run when they change settings and want it run immediately. " +
        "context is optional and only includes fields to change: notes, cadence, jobId, channel, campaignDailyBudget, campaignDesiredStatus, campaignDestinationUrl, campaignCountryCode. " +
        "channel is meta, linkedin, or both. campaignDesiredStatus is active or paused. jobId is a GUID or empty for clear.";

    public static string UserPrompt(AgentContextDto context, IReadOnlyList<AgentChatMessageDto> messages)
    {
        var text = new StringBuilder();
        text.AppendLine("VERA MEMORY:");
        text.AppendLine($"Job: {(context.JobId is null ? "(none)" : context.JobId.Value.ToString())}");
        text.AppendLine($"Platforms: {string.Join(", ", VeraCampaignPlaybook.NormalizePlatforms(context.Channel))}");
        text.AppendLine($"Daily budget USD: {context.CampaignDailyBudget}");
        text.AppendLine($"Desired status: {context.CampaignDesiredStatus}");
        text.AppendLine($"Destination URL: {Blank(context.CampaignDestinationUrl)}");
        text.AppendLine($"Country: {Blank(context.CampaignCountryCode)}");
        text.AppendLine($"Cadence: {Blank(context.Cadence)}; next run: {context.NextRunAt:u}");
        text.AppendLine($"Notes: {Blank(context.Notes)}");
        text.AppendLine($"Campaigns managed (lifetime): {context.ConvertedCandidates}");
        text.AppendLine("LAST RUN:");
        text.AppendLine(Blank(context.LastSummary));
        if (context.Campaigns is { Count: > 0 })
        {
            text.AppendLine("RECENT CAMPAIGNS:");
            foreach (var item in context.Campaigns.Take(6))
                text.AppendLine($"- {item.Platform} {item.Name} ({item.Status}/{item.Action}) id={item.CampaignId}");
        }

        if (context.Reviews is { Count: > 0 })
        {
            text.AppendLine("RECENT RUNS:");
            foreach (var review in context.Reviews.Take(5))
                text.AppendLine($"- {review.At:u}: {review.Summary}");
        }

        text.AppendLine();
        text.AppendLine("CHAT:");
        foreach (var message in messages.TakeLast(24))
        {
            var role = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "Vera" : "User";
            text.Append(role).Append(": ").AppendLine((message.Content ?? string.Empty).Trim());
        }

        var packed = text.ToString();
        return packed.Length > 14000 ? packed[..14000] : packed;
    }

    public static AriaChatDecisionDto Parse(string? content)
    {
        var raw = (content ?? string.Empty).Trim();
        if (raw.Length == 0)
            return new AriaChatDecisionDto { Reply = "I could not answer that.", Action = "none" };

        try
        {
            var json = ExtractJsonObject(raw) ?? raw;
            var parsed = JsonSerializer.Deserialize<AriaChatDecisionDto>(json, JsonOptions);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Reply))
                return new AriaChatDecisionDto { Reply = raw, Action = "none" };
            parsed.Action = NormalizeAction(parsed.Action);
            return parsed;
        }
        catch (JsonException)
        {
            return new AriaChatDecisionDto { Reply = raw, Action = "none" };
        }
    }

    public static void ApplyPatch(AgentContextDto target, AriaChatContextPatchDto? patch)
    {
        if (patch is null) return;
        AriaChatInterpreter.ApplyPatch(target, patch);

        if (patch.CampaignDailyBudget is > 0)
            target.CampaignDailyBudget = VeraCampaignPlaybook.ClampDailyBudget(patch.CampaignDailyBudget);
        if (!string.IsNullOrWhiteSpace(patch.CampaignDesiredStatus))
            target.CampaignDesiredStatus = VeraCampaignPlaybook.NormalizeDesiredStatus(patch.CampaignDesiredStatus);
        if (patch.CampaignDestinationUrl is not null)
            target.CampaignDestinationUrl = VeraCampaignPlaybook.NormalizeDestinationUrl(patch.CampaignDestinationUrl);
        if (!string.IsNullOrWhiteSpace(patch.CampaignCountryCode))
            target.CampaignCountryCode = VeraCampaignPlaybook.NormalizeCountryCode(patch.CampaignCountryCode);
        if (!string.IsNullOrWhiteSpace(patch.Channel))
            target.Channel = patch.Channel.Trim().ToLowerInvariant();
    }

    public static bool WantsRun(string action) => AriaChatInterpreter.WantsRun(action);
    public static bool WantsUpdate(string action) => AriaChatInterpreter.WantsUpdate(action);

    private static string NormalizeAction(string? action)
    {
        var value = (action ?? "none").Trim().ToLowerInvariant().Replace('-', '_');
        return value is "run_now" or "update_context" or "update_and_run" ? value : "none";
    }

    private static string Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(empty)" : value.Trim();

    private static string? ExtractJsonObject(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = trimmed.IndexOf('\n');
            if (firstNewLine >= 0)
                trimmed = trimmed[(firstNewLine + 1)..];
            var fenceEnd = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0)
                trimmed = trimmed[..fenceEnd];
            trimmed = trimmed.Trim();
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        return trimmed[start..(end + 1)];
    }
}
