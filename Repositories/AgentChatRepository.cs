using Dapper;
using nexthire_api.Data;
using nexthire_api.DTOs.AiAgents;

namespace nexthire_api.Repositories;

public sealed class AgentChatRepository : IAgentChatRepository
{
    public const string EnsureSql = @"
            IF OBJECT_ID(N'dbo.nh_agent_chat_messages', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.nh_agent_chat_messages (
                    id uniqueidentifier NOT NULL CONSTRAINT DF_nh_agent_chat_id DEFAULT NEWSEQUENTIALID() PRIMARY KEY,
                    org_id uniqueidentifier NOT NULL,
                    agent_id nvarchar(32) NOT NULL,
                    role nvarchar(16) NOT NULL,
                    content nvarchar(max) NOT NULL,
                    created_at datetimeoffset(7) NOT NULL CONSTRAINT DF_nh_agent_chat_created DEFAULT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'),
                    CONSTRAINT CK_nh_agent_chat_role CHECK (role IN (N'user', N'assistant'))
                );
                CREATE INDEX IX_nh_agent_chat_org_agent ON dbo.nh_agent_chat_messages (org_id, agent_id, created_at ASC);
            END;";

    private const int MaxContent = 4000;
    private const int MaxTake = 200;

    private readonly IDbConnectionFactory _connectionFactory;

    public AgentChatRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task EnsureSchemaAsync()
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(EnsureSql);
    }

    public async Task<IReadOnlyList<AgentChatMessageDto>> ListAsync(
        Guid orgId,
        string agentId,
        int take = 80,
        CancellationToken cancellationToken = default)
    {
        if (orgId == Guid.Empty || string.IsNullOrWhiteSpace(agentId))
            return [];

        var limit = Math.Clamp(take, 1, MaxTake);
        const string sql = @"
            SELECT role AS Role, content AS Content, created_at AS At
            FROM (
                SELECT TOP (@Take) role, content, created_at
                FROM dbo.nh_agent_chat_messages
                WHERE org_id = @OrgId AND agent_id = @AgentId
                ORDER BY created_at DESC
            ) recent
            ORDER BY created_at ASC;";

        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<AgentChatMessageDto>(new CommandDefinition(
            sql,
            new { OrgId = orgId, AgentId = agentId.Trim(), Take = limit },
            cancellationToken: cancellationToken));
        return rows.ToList();
    }

    public async Task AppendAsync(
        Guid orgId,
        string agentId,
        string role,
        string content,
        CancellationToken cancellationToken = default)
    {
        var text = (content ?? string.Empty).Trim();
        if (orgId == Guid.Empty || string.IsNullOrWhiteSpace(agentId) || text.Length == 0)
            return;

        var normalizedRole = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
            ? "assistant"
            : "user";
        if (text.Length > MaxContent)
            text = text[..MaxContent];

        const string sql = @"
            INSERT INTO dbo.nh_agent_chat_messages (org_id, agent_id, role, content)
            VALUES (@OrgId, @AgentId, @Role, @Content);";

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            OrgId = orgId,
            AgentId = agentId.Trim(),
            Role = normalizedRole,
            Content = text,
        }, cancellationToken: cancellationToken));
    }
}
