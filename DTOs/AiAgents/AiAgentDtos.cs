using System.Text.Json;
using System.Text.Json.Serialization;

namespace nexthire_api.DTOs.AiAgents;

public class HiredAgentDto
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public string AgentId { get; set; } = string.Empty;
    public string Status { get; set; } = "active";
    public int Runs { get; set; }
    public long Tokens { get; set; }
    public DateTimeOffset HiredAt { get; set; }
    public Guid? HiredBy { get; set; }
    public AgentContextDto? Context { get; set; }
}

public class AriaReviewDto
{
    public string Key { get; set; } = "score+convert";
    public int Scanned { get; set; }
    public int Scored { get; set; }
    public int Converted { get; set; }
    public int Quota { get; set; }
    public int Skipped { get; set; }
    public int Errors { get; set; }
    public long Tokens { get; set; }
    public string? Next { get; set; }
    public string? Summary { get; set; }
    public DateTimeOffset At { get; set; }
}

public class VeraCampaignDto
{
    public Guid CampaignId { get; set; }
    public Guid? JobId { get; set; }
    public string Platform { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "paused";
    public string Action { get; set; } = "created";
    public DateTimeOffset At { get; set; }
}

public class KaiOutreachDto
{
    public Guid CandidateId { get; set; }
    public Guid? ApplicationId { get; set; }
    public string Channel { get; set; } = "email";
    public string Action { get; set; } = "intro";
    public DateTimeOffset At { get; set; }
}

public class AgentContextDto
{
    public Guid? JobId { get; set; }
    /// <summary>Kai: pipeline stage to message; null resolves to the org "Screening" stage.</summary>
    public Guid? StageId { get; set; }
    public string Cadence { get; set; } = "daily";
    public string Channel { get; set; } = "email";
    public string TemplateId { get; set; } = string.Empty;
    public int ResponseWaitHours { get; set; } = 48;
    public int MaxProcessed { get; set; } = 100;
    public int AutoConversionPercent { get; set; } = 10;
    public int MaxOffersToSend { get; set; } = 10;
    public string Notes { get; set; } = string.Empty;
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    /// <summary>Short machine-readable plan label from the last run (e.g. score+convert).</summary>
    public string LastPlan { get; set; } = string.Empty;
    /// <summary>Human-readable summary of the last run outcome.</summary>
    public string LastSummary { get; set; } = string.Empty;
    /// <summary>Tokens spent in agent chat (tracked separately from run tokens).</summary>
    public long ChatTokens { get; set; }
    /// <summary>Recent agent run outcomes used for activity / KPIs / trends.</summary>
    public List<AriaReviewDto> Reviews { get; set; } = [];
    /// <summary>Lifetime results (Aria conversions / Vera campaigns / Kai outreach).</summary>
    public int ConvertedCandidates { get; set; }

    /// <summary>Vera: daily budget USD for Meta/LinkedIn campaigns.</summary>
    public decimal CampaignDailyBudget { get; set; } = 10;
    /// <summary>Vera: active or paused when creating/syncing.</summary>
    public string CampaignDesiredStatus { get; set; } = "paused";
    /// <summary>Vera: public job / landing URL for traffic ads.</summary>
    public string CampaignDestinationUrl { get; set; } = string.Empty;
    /// <summary>Vera: ISO country for Meta special ad categories.</summary>
    public string CampaignCountryCode { get; set; } = "US";
    /// <summary>Vera: stamped campaigns created or synced by the agent.</summary>
    public List<VeraCampaignDto> Campaigns { get; set; } = [];
    /// <summary>Kai: stamped screening outreach (intro / follow-up) per candidate.</summary>
    public List<KaiOutreachDto> Outreach { get; set; } = [];

    public static AgentContextDto? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<AgentContextDto>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public class AgentCatalogItemDto
{
    public string AgentId { get; set; } = string.Empty;
    public string Availability { get; set; } = "v1";
    public bool Hireable { get; set; }
    public bool Hired { get; set; }
    public DateTimeOffset? HiredAt { get; set; }
}

public class UpdateAgentStatusRequestDto
{
    public string Status { get; set; } = string.Empty;
}

public class UpdateAgentContextRequestDto : AgentContextDto
{
}

public class AgentChatMessageDto
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; }
}

public class AgentChatRequestDto
{
    public IReadOnlyList<AgentChatMessageDto> Messages { get; set; } = Array.Empty<AgentChatMessageDto>();
    public string? Locale { get; set; }
}

public class AgentChatReplyDto
{
    public string Reply { get; set; } = string.Empty;
    public string Action { get; set; } = "none";
    public HiredAgentDto? Agent { get; set; }
    public List<AgentChatMessageDto> Messages { get; set; } = [];
}

public class AgentChatThreadDto
{
    public List<AgentChatMessageDto> Messages { get; set; } = [];
}

public class AriaChatContextPatchDto
{
    public string? Notes { get; set; }
    public string? Cadence { get; set; }
    public int? MaxProcessed { get; set; }
    public int? AutoConversionPercent { get; set; }
    /// <summary>GUID to set, empty string to clear (all jobs), null to leave unchanged.</summary>
    public string? JobId { get; set; }
    public string? Channel { get; set; }
    public decimal? CampaignDailyBudget { get; set; }
    public string? CampaignDesiredStatus { get; set; }
    public string? CampaignDestinationUrl { get; set; }
    public string? CampaignCountryCode { get; set; }
    public int? ResponseWaitHours { get; set; }
    public int? MaxOffersToSend { get; set; }
    public string? TemplateId { get; set; }
    /// <summary>GUID to set, empty string to clear (default stage), null to leave unchanged.</summary>
    public string? StageId { get; set; }
}

public class AriaChatDecisionDto
{
    public string Reply { get; set; } = string.Empty;
    public string Action { get; set; } = "none";
    public AriaChatContextPatchDto? Context { get; set; }
}

public class TokenUsageEventDto
{
    public DateTimeOffset At { get; set; }
    public int Tokens { get; set; }
    public string? AgentId { get; set; }
    public string Source { get; set; } = string.Empty;
}

public class AgentTokenUsageDto
{
    public long Total { get; set; }
    public List<TokenUsageEventDto> Events { get; set; } = [];
}
