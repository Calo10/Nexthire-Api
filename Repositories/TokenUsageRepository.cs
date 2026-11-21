using Dapper;
using nexthire_api.Data;
using nexthire_api.DTOs.AiAgents;

namespace nexthire_api.Repositories;

public sealed class TokenUsageRepository : ITokenUsageRepository
{
    public const string EnsureSql = @"
            IF OBJECT_ID(N'dbo.nh_token_usage', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.nh_token_usage (
                    id uniqueidentifier NOT NULL CONSTRAINT DF_nh_token_usage_id DEFAULT NEWSEQUENTIALID() PRIMARY KEY,
                    org_id uniqueidentifier NOT NULL,
                    agent_id nvarchar(32) NULL,
                    source nvarchar(32) NOT NULL,
                    entity_type nvarchar(32) NULL,
                    prompt_tokens int NOT NULL CONSTRAINT DF_nh_token_usage_prompt DEFAULT 0,
                    completion_tokens int NOT NULL CONSTRAINT DF_nh_token_usage_completion DEFAULT 0,
                    total_tokens int NOT NULL CONSTRAINT DF_nh_token_usage_total DEFAULT 0,
                    created_at datetimeoffset(7) NOT NULL CONSTRAINT DF_nh_token_usage_created DEFAULT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
                );
                CREATE INDEX IX_nh_token_usage_org_created ON dbo.nh_token_usage (org_id, created_at DESC);
                CREATE INDEX IX_nh_token_usage_org_agent ON dbo.nh_token_usage (org_id, agent_id, created_at DESC);
            END;";

    private readonly IDbConnectionFactory _connectionFactory;

    public TokenUsageRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task EnsureSchemaAsync()
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(EnsureSql);
    }

    public async Task RecordAsync(TokenUsageEntry entry, CancellationToken cancellationToken = default)
    {
        if (entry.OrgId == Guid.Empty || entry.TotalTokens <= 0)
            return;

        const string sql = @"
            INSERT INTO dbo.nh_token_usage
                (org_id, agent_id, source, entity_type, prompt_tokens, completion_tokens, total_tokens)
            VALUES
                (@OrgId, @AgentId, @Source, @EntityType, @PromptTokens, @CompletionTokens, @TotalTokens);";

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            entry.OrgId,
            AgentId = Truncate(entry.AgentId, 32),
            Source = Truncate(string.IsNullOrWhiteSpace(entry.Source) ? "ai" : entry.Source, 32) ?? "ai",
            EntityType = Truncate(entry.EntityType, 32),
            PromptTokens = Math.Max(0, entry.PromptTokens),
            CompletionTokens = Math.Max(0, entry.CompletionTokens),
            TotalTokens = Math.Max(0, entry.TotalTokens),
        }, cancellationToken: cancellationToken));
    }

    public async Task<long> SumByAgentAsync(Guid orgId, string agentId, CancellationToken cancellationToken = default)
    {
        if (orgId == Guid.Empty || string.IsNullOrWhiteSpace(agentId))
            return 0;

        const string sql = @"
            SELECT ISNULL(SUM(CAST(total_tokens AS bigint)), 0)
            FROM dbo.nh_token_usage
            WHERE org_id = @OrgId AND agent_id = @AgentId;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            sql,
            new { OrgId = orgId, AgentId = agentId.Trim() },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<TokenUsageEventDto>> ListDailyAsync(
        Guid orgId,
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
            SELECT
                TODATETIMEOFFSET(
                    DATEADD(HOUR, 12, CAST(CAST(SWITCHOFFSET(created_at, '+00:00') AS date) AS datetime2)),
                    '+00:00') AS At,
                CAST(SUM(CAST(total_tokens AS bigint)) AS int) AS Tokens,
                agent_id AS AgentId,
                source AS Source
            FROM dbo.nh_token_usage
            WHERE org_id = @OrgId
              AND created_at >= @Since
              AND agent_id IS NOT NULL
              AND total_tokens > 0
            GROUP BY CAST(SWITCHOFFSET(created_at, '+00:00') AS date), agent_id, source
            ORDER BY At ASC;";

        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<TokenUsageEventDto>(new CommandDefinition(
            sql,
            new { OrgId = orgId, Since = since },
            cancellationToken: cancellationToken));
        return rows.ToList();
    }

    private static string? Truncate(string? value, int max)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return null;
        return text.Length <= max ? text : text[..max];
    }
}
