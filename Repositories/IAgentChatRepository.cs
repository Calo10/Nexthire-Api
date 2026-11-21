using nexthire_api.DTOs.AiAgents;

namespace nexthire_api.Repositories;

public interface IAgentChatRepository
{
    Task EnsureSchemaAsync();
    Task<IReadOnlyList<AgentChatMessageDto>> ListAsync(Guid orgId, string agentId, int take = 80, CancellationToken cancellationToken = default);
    Task AppendAsync(Guid orgId, string agentId, string role, string content, CancellationToken cancellationToken = default);
}
