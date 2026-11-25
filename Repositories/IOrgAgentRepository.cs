using nexthire_api.DTOs.AiAgents;

namespace nexthire_api.Repositories;

public interface IOrgAgentRepository
{
    Task EnsureSchemaAsync();
    Task<IReadOnlyList<HiredAgentDto>> ListAsync(Guid orgId);
    Task<HiredAgentDto?> GetByAgentIdAsync(Guid orgId, string agentId);
    Task<IReadOnlyList<HiredAgentDto>> ListActiveByAgentIdAsync(string agentId);
    Task<HiredAgentDto> InsertAsync(Guid orgId, string agentId, Guid? hiredBy);
    Task<bool> DeleteAsync(Guid orgId, string agentId);
    Task<HiredAgentDto?> UpdateStatusAsync(Guid orgId, string agentId, string status);
    Task<HiredAgentDto?> UpdateContextAsync(Guid orgId, string agentId, string contextJson);
    Task<HiredAgentDto?> IncrementRunAsync(Guid orgId, string agentId);
    Task<HiredAgentDto?> SetOperationalStatsAsync(Guid orgId, string agentId, int runs, long tokens, string status);
}
