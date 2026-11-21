using nexthire_api.DTOs.AiAgents;

namespace nexthire_api.Repositories;

public sealed class TokenUsageEntry
{
    public Guid OrgId { get; set; }
    public string? AgentId { get; set; }
    public string Source { get; set; } = "ai";
    public string? EntityType { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
}

public interface ITokenUsageRepository
{
    Task EnsureSchemaAsync();
    Task RecordAsync(TokenUsageEntry entry, CancellationToken cancellationToken = default);
    Task<long> SumByAgentAsync(Guid orgId, string agentId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TokenUsageEventDto>> ListDailyAsync(Guid orgId, DateTimeOffset since, CancellationToken cancellationToken = default);
}
