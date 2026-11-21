using System.Data.SqlClient;
using Dapper;
using nexthire_api.Data;
using nexthire_api.DTOs.AiAgents;

namespace nexthire_api.Repositories;

public class OrgAgentRepository : IOrgAgentRepository
{
    private const string SelectColumns = @"
        id AS Id,
        org_id AS OrgId,
        agent_id AS AgentId,
        status AS Status,
        runs AS Runs,
        tokens AS Tokens,
        hired_at AS HiredAt,
        hired_by AS HiredBy,
        context AS ContextJson";

    private const string InsertedColumns = @"
        INSERTED.id AS Id,
        INSERTED.org_id AS OrgId,
        INSERTED.agent_id AS AgentId,
        INSERTED.status AS Status,
        INSERTED.runs AS Runs,
        INSERTED.tokens AS Tokens,
        INSERTED.hired_at AS HiredAt,
        INSERTED.hired_by AS HiredBy,
        INSERTED.context AS ContextJson";

    private readonly IDbConnectionFactory _connectionFactory;

    public OrgAgentRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task EnsureSchemaAsync()
    {
        const string sql = @"
IF OBJECT_ID(N'dbo.nh_org_agents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.nh_org_agents
    (
        id UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_nh_org_agents_id DEFAULT NEWSEQUENTIALID() PRIMARY KEY,
        org_id UNIQUEIDENTIFIER NOT NULL,
        agent_id NVARCHAR(32) NOT NULL,
        status NVARCHAR(20) NOT NULL CONSTRAINT DF_nh_org_agents_status DEFAULT N'active',
        hired_by UNIQUEIDENTIFIER NULL,
        hired_at DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_nh_org_agents_hired_at DEFAULT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'),
        updated_at DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_nh_org_agents_updated_at DEFAULT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'),
        runs INT NOT NULL CONSTRAINT DF_nh_org_agents_runs DEFAULT 0,
        tokens BIGINT NOT NULL CONSTRAINT DF_nh_org_agents_tokens DEFAULT 0,
        context NVARCHAR(MAX) NULL,
        CONSTRAINT UQ_nh_org_agents_org_agent UNIQUE (org_id, agent_id)
    );
    CREATE INDEX IX_nh_org_agents_org ON dbo.nh_org_agents (org_id, hired_at DESC);
END";

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(sql);
    }

    public async Task<IReadOnlyList<HiredAgentDto>> ListAsync(Guid orgId)
    {
        var sql = $@"
            SELECT {SelectColumns}
            FROM dbo.nh_org_agents
            WHERE org_id = @OrgId
            ORDER BY hired_at ASC;";

        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<OrgAgentRow>(sql, new { OrgId = orgId });
        return rows.Select(Map).ToList();
    }

    public async Task<HiredAgentDto?> GetByAgentIdAsync(Guid orgId, string agentId)
    {
        var sql = $@"
            SELECT {SelectColumns}
            FROM dbo.nh_org_agents
            WHERE org_id = @OrgId AND agent_id = @AgentId;";

        using var connection = _connectionFactory.CreateConnection();
        var row = await connection.QueryFirstOrDefaultAsync<OrgAgentRow>(sql, new { OrgId = orgId, AgentId = agentId });
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<HiredAgentDto>> ListActiveByAgentIdAsync(string agentId)
    {
        var sql = $@"
            SELECT {SelectColumns}
            FROM dbo.nh_org_agents
            WHERE agent_id = @AgentId
              AND status IN (N'active', N'busy')
            ORDER BY hired_at ASC;";

        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<OrgAgentRow>(sql, new { AgentId = agentId });
        return rows.Select(Map).ToList();
    }

    public async Task<HiredAgentDto> InsertAsync(Guid orgId, string agentId, Guid? hiredBy)
    {
        var sql = $@"
            INSERT INTO dbo.nh_org_agents (org_id, agent_id, status, hired_by)
            OUTPUT {InsertedColumns}
            VALUES (@OrgId, @AgentId, N'active', @HiredBy);";

        using var connection = _connectionFactory.CreateConnection();
        try
        {
            var row = await connection.QuerySingleAsync<OrgAgentRow>(sql, new
            {
                OrgId = orgId,
                AgentId = agentId,
                HiredBy = hiredBy,
            });
            return Map(row);
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            var existing = await GetByAgentIdAsync(orgId, agentId);
            if (existing is null) throw;
            return existing;
        }
    }

    public async Task<bool> DeleteAsync(Guid orgId, string agentId)
    {
        const string sql = @"
            DELETE FROM dbo.nh_org_agents
            WHERE org_id = @OrgId AND agent_id = @AgentId;";

        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.ExecuteAsync(sql, new { OrgId = orgId, AgentId = agentId });
        return rows > 0;
    }

    public async Task<HiredAgentDto?> UpdateStatusAsync(Guid orgId, string agentId, string status)
    {
        var sql = $@"
            UPDATE dbo.nh_org_agents
            SET status = @Status,
                updated_at = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            OUTPUT {InsertedColumns}
            WHERE org_id = @OrgId AND agent_id = @AgentId;";

        using var connection = _connectionFactory.CreateConnection();
        var row = await connection.QueryFirstOrDefaultAsync<OrgAgentRow>(sql, new
        {
            OrgId = orgId,
            AgentId = agentId,
            Status = status,
        });
        return row is null ? null : Map(row);
    }

    public async Task<HiredAgentDto?> UpdateContextAsync(Guid orgId, string agentId, string contextJson)
    {
        var sql = $@"
            UPDATE dbo.nh_org_agents
            SET context = @ContextJson,
                updated_at = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            OUTPUT {InsertedColumns}
            WHERE org_id = @OrgId AND agent_id = @AgentId;";

        using var connection = _connectionFactory.CreateConnection();
        var row = await connection.QueryFirstOrDefaultAsync<OrgAgentRow>(sql, new
        {
            OrgId = orgId,
            AgentId = agentId,
            ContextJson = contextJson,
        });
        return row is null ? null : Map(row);
    }

    public async Task<HiredAgentDto?> IncrementRunAsync(Guid orgId, string agentId)
    {
        var sql = $@"
            UPDATE dbo.nh_org_agents
            SET runs = runs + 1,
                status = CASE WHEN status = N'paused' THEN status ELSE N'busy' END,
                updated_at = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            OUTPUT {InsertedColumns}
            WHERE org_id = @OrgId AND agent_id = @AgentId;";

        using var connection = _connectionFactory.CreateConnection();
        var row = await connection.QueryFirstOrDefaultAsync<OrgAgentRow>(sql, new { OrgId = orgId, AgentId = agentId });
        return row is null ? null : Map(row);
    }

    public async Task<HiredAgentDto?> SetOperationalStatsAsync(
        Guid orgId,
        string agentId,
        int runs,
        long tokens,
        string status)
    {
        var sql = $@"
            UPDATE dbo.nh_org_agents
            SET runs = @Runs,
                tokens = @Tokens,
                status = CASE WHEN status = N'paused' THEN status ELSE @Status END,
                updated_at = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            OUTPUT {InsertedColumns}
            WHERE org_id = @OrgId AND agent_id = @AgentId;";

        using var connection = _connectionFactory.CreateConnection();
        var row = await connection.QueryFirstOrDefaultAsync<OrgAgentRow>(sql, new
        {
            OrgId = orgId,
            AgentId = agentId,
            Runs = Math.Max(0, runs),
            Tokens = Math.Max(0L, tokens),
            Status = status,
        });
        return row is null ? null : Map(row);
    }

    private static HiredAgentDto Map(OrgAgentRow row) => new()
    {
        Id = row.Id,
        OrgId = row.OrgId,
        AgentId = row.AgentId,
        Status = row.Status,
        Runs = row.Runs,
        Tokens = row.Tokens,
        HiredAt = row.HiredAt,
        HiredBy = row.HiredBy,
        Context = AgentContextDto.Parse(row.ContextJson),
    };

    private sealed class OrgAgentRow
    {
        public Guid Id { get; set; }
        public Guid OrgId { get; set; }
        public string AgentId { get; set; } = string.Empty;
        public string Status { get; set; } = "active";
        public int Runs { get; set; }
        public long Tokens { get; set; }
        public DateTimeOffset HiredAt { get; set; }
        public Guid? HiredBy { get; set; }
        public string? ContextJson { get; set; }
    }
}
