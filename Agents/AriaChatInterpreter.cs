using System.Text;
using System.Text.Json;
using nexthire_api.DTOs.AiAgents;

namespace nexthire_api.Agents;

public static class AriaChatInterpreter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string SystemPrompt(string language) =>
        "You are Aria, the NextHire sourcing specialist. You score sourced leads and convert strong matches into pipeline candidates. " +
        "Answer from the provided memory, last run summary, and this org's saved conversation. " +
        "If the user asks what you did last, summarize LAST RUN. " +
        "If settings are missing, ask for them. " +
        $"Reply in {language}. Return JSON only with keys: reply, action, context. " +
        "action is one of: none, run_now, update_context, update_and_run. " +
        "Use update_context when the user changes job filter, cadence, conversion %, max processed, or notes. " +
        "Use run_now when they want a score+convert pass now. Use update_and_run when they change settings and want it run immediately. " +
        "context is optional and only includes fields to change: notes, cadence, maxProcessed, autoConversionPercent, jobId. " +
        "jobId should be a GUID string when they pick a specific job, or empty string for all jobs. Do not invent lead names.";

    public static string UserPrompt(AgentContextDto context, IReadOnlyList<AgentChatMessageDto> messages)
    {
        var text = new StringBuilder();
        text.AppendLine("ARIA MEMORY:");
        text.AppendLine($"Job filter: {(context.JobId is null ? "all jobs" : context.JobId.Value.ToString())}");
        text.AppendLine($"Cadence: {Blank(context.Cadence)}");
        text.AppendLine($"Auto conversion %: {context.AutoConversionPercent}");
        text.AppendLine($"Max processed / run: {context.MaxProcessed}");
        text.AppendLine($"Notes: {Blank(context.Notes)}");
        text.AppendLine($"Next run: {context.NextRunAt:u}");
        text.AppendLine($"Last plan: {Blank(context.LastPlan)}");
        text.AppendLine($"Converted candidates (lifetime): {context.ConvertedCandidates}");
        text.AppendLine("LAST RUN:");
        text.AppendLine(Blank(context.LastSummary));
        if (context.Reviews is { Count: > 0 })
        {
            text.AppendLine("RECENT RUNS:");
            foreach (var review in context.Reviews.Take(5))
            {
                text.AppendLine(
                    $"- {review.At:u}: scanned {review.Scanned}, scored {review.Scored}, converted {review.Converted}, tokens {review.Tokens}");
            }
        }
        text.AppendLine();
        text.AppendLine("CHAT:");
        foreach (var message in messages.TakeLast(24))
        {
            var role = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "Aria" : "User";
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
        if (!string.IsNullOrWhiteSpace(patch.Notes)) target.Notes = patch.Notes.Trim();
        if (!string.IsNullOrWhiteSpace(patch.Cadence))
            target.Cadence = AriaSourcingPlaybook.NormalizeCadence(patch.Cadence);
        if (patch.MaxProcessed is > 0)
            target.MaxProcessed = AriaSourcingPlaybook.ClampMaxProcessed(patch.MaxProcessed);
        if (patch.AutoConversionPercent is >= 0)
            target.AutoConversionPercent = AriaSourcingPlaybook.ClampAutoConversionPercent(patch.AutoConversionPercent);

        if (patch.JobId is null) return;
        var raw = patch.JobId.Trim();
        if (raw.Length == 0)
            target.JobId = null;
        else if (Guid.TryParse(raw, out var jobId))
            target.JobId = jobId;
    }

    public static bool WantsRun(string action) =>
        action is "run_now" or "update_and_run";

    public static bool WantsUpdate(string action) =>
        action is "update_context" or "update_and_run";

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
