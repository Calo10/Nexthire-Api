using System.Text;
using System.Text.Json;
using nexthire_api.DTOs.AiAgents;

namespace nexthire_api.Agents;

public static class LenaChatInterpreter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string SystemPrompt(string language) =>
        "You are Lena, the NextHire interview specialist. You send interview invites and reminders by email or WhatsApp. " +
        "Answer from the provided memory, last run summary, outreach stamps, and this org's saved conversation. " +
        "If the user asks what you did last, summarize LAST RUN. " +
        "Email needs a template; WhatsApp uses the org Twilio introduction/follow-up content. " +
        $"Reply in {language}. Return JSON only with keys: reply, action, context. " +
        "action is one of: none, run_now, update_context, update_and_run. " +
        "Use update_context when the user changes job, stage, channel, template, wait hours, max processed, cadence, or notes. " +
        "Use run_now when they want an outreach pass now. Use update_and_run when they change settings and want it run immediately. " +
        "context is optional and only includes fields to change: notes, cadence, maxProcessed, responseWaitHours, channel, templateId, jobId, stageId. " +
        "channel is email or whatsapp. jobId and stageId are GUID strings or empty to clear.";

    public static string UserPrompt(AgentContextDto context, IReadOnlyList<AgentChatMessageDto> messages)
    {
        var text = new StringBuilder();
        text.AppendLine("LENA MEMORY:");
        text.AppendLine($"Job: {(context.JobId is null ? "all jobs" : context.JobId.Value.ToString())}");
        text.AppendLine($"Stage: {(context.StageId is null ? $"default {LenaInterviewPlaybook.DefaultStageName}" : context.StageId.Value.ToString())}");
        text.AppendLine($"Channel: {LenaInterviewPlaybook.NormalizeChannel(context.Channel)}");
        text.AppendLine($"TemplateId: {Blank(context.TemplateId)}");
        text.AppendLine($"Response wait hours: {context.ResponseWaitHours}");
        text.AppendLine($"Max processed / run: {context.MaxProcessed}");
        text.AppendLine($"Cadence: {Blank(context.Cadence)}; next run: {context.NextRunAt:u}");
        text.AppendLine($"Notes: {Blank(context.Notes)}");
        text.AppendLine($"Interview messages sent (lifetime): {context.ConvertedCandidates}");
        text.AppendLine("LAST RUN:");
        text.AppendLine(Blank(context.LastSummary));
        if (context.Outreach is { Count: > 0 })
        {
            text.AppendLine("RECENT OUTREACH:");
            foreach (var item in context.Outreach.Take(8))
                text.AppendLine($"- {item.Channel} {item.Action} candidate={item.CandidateId} at {item.At:u}");
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
            var role = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "Lena" : "User";
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

        if (!string.IsNullOrWhiteSpace(patch.Channel))
            target.Channel = LenaInterviewPlaybook.NormalizeChannel(patch.Channel);
        if (patch.ResponseWaitHours is > 0)
            target.ResponseWaitHours = LenaInterviewPlaybook.ClampResponseWaitHours(patch.ResponseWaitHours);
        if (patch.TemplateId is not null)
            target.TemplateId = patch.TemplateId.Trim();
        if (patch.StageId is null) return;

        var raw = patch.StageId.Trim();
        if (raw.Length == 0)
            target.StageId = null;
        else if (Guid.TryParse(raw, out var stageId))
            target.StageId = stageId;
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
