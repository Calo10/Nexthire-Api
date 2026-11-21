using nexthire_api.DTOs.AiAgents;

namespace nexthire_api.Services;

public interface IAiAgentService
{
    Task<IReadOnlyList<AgentCatalogItemDto>> ListCatalogAsync(Guid orgId);
    Task<IReadOnlyList<HiredAgentDto>> ListHiredAsync(Guid orgId);
    Task<HiredAgentDto?> GetHiredAsync(Guid orgId, string agentId);
    Task<(HiredAgentDto Agent, bool Created)> HireAsync(Guid orgId, string agentId, Guid? hiredBy);
    Task<bool> UnhireAsync(Guid orgId, string agentId);
    Task<HiredAgentDto?> SetStatusAsync(Guid orgId, string agentId, string status);
    Task<HiredAgentDto?> UpdateContextAsync(Guid orgId, string agentId, AgentContextDto context);
    Task<HiredAgentDto?> RunNowAsync(
        Guid orgId,
        string agentId,
        Guid nexaUserId,
        string? userEmail,
        string? displayName,
        CancellationToken cancellationToken = default);

    Task<AgentChatThreadDto?> ListChatAsync(Guid orgId, string agentId, CancellationToken cancellationToken = default);

    Task<AgentChatReplyDto?> ChatAsync(
        Guid orgId,
        string agentId,
        AgentChatRequestDto request,
        Guid nexaUserId,
        string? userEmail,
        string? displayName,
        CancellationToken cancellationToken = default);

    Task<AgentTokenUsageDto> GetUsageAsync(Guid orgId, int days = 90, CancellationToken cancellationToken = default);

    Task RunDueAgentsAsync(CancellationToken cancellationToken = default);
}
