using Dapper;
using nexthire_api.Data;
using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public class PipelineRepository : IPipelineRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<PipelineRepository> _logger;

    public PipelineRepository(IDbConnectionFactory connectionFactory, ILogger<PipelineRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PipelineStageDto>> GetStagesAsync(Guid orgId)
    {
        const string sql = @"
            SELECT
                id AS Id,
                name AS Name,
                sort_order AS SortOrder,
                CAST(is_system AS bit) AS IsSystem
            FROM pipeline_stages
            WHERE org_id = @orgId
            ORDER BY sort_order ASC;";

        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<PipelineStageDto>(sql, new { orgId });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<PipelineStageDto>> EnsureDefaultStagesAsync(Guid orgId)
    {
        // If no stages exist for org, create defaults.
        var existing = await GetStagesAsync(orgId);
        if (existing.Count > 0)
            return existing;

        var defaults = new[]
        {
            ("Applied", 1),
            ("Screening", 2),
            ("Interview", 3),
            ("Offer", 4),
            ("Hired", 5)
        };

        const string insertSql = @"
            INSERT INTO pipeline_stages (id, org_id, name, sort_order, is_system)
            VALUES (@id, @orgId, @name, @sortOrder, 1);";

        using var connection = _connectionFactory.CreateConnection();
        foreach (var (name, sortOrder) in defaults)
        {
            await connection.ExecuteAsync(insertSql, new { id = Guid.NewGuid(), orgId, name, sortOrder });
        }

        return await GetStagesAsync(orgId);
    }

    public async Task<Guid?> GetFirstStageIdAsync(Guid orgId)
    {
        const string sql = @"
            SELECT TOP 1 id
            FROM pipeline_stages
            WHERE org_id = @orgId
            ORDER BY sort_order ASC;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<Guid?>(sql, new { orgId });
    }

    public async Task<bool> StageExistsInOrgAsync(Guid orgId, Guid stageId)
    {
        const string sql = @"SELECT CASE WHEN EXISTS (SELECT 1 FROM pipeline_stages WHERE org_id = @orgId AND id = @stageId) THEN 1 ELSE 0 END;";
        using var connection = _connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<int>(sql, new { orgId, stageId }) == 1;
    }

    public async Task<bool> JobExistsInOrgAsync(Guid orgId, Guid jobId)
    {
        const string sql = @"SELECT CASE WHEN EXISTS (SELECT 1 FROM jobs WHERE org_id = @orgId AND id = @jobId) THEN 1 ELSE 0 END;";
        using var connection = _connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<int>(sql, new { orgId, jobId }) == 1;
    }

    public async Task<Guid?> GetJobOrgIdAsync(Guid jobId)
    {
        const string sql = @"SELECT org_id FROM jobs WHERE id = @jobId;";
        using var connection = _connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<Guid?>(sql, new { jobId });
    }

    public async Task<JobBotContextDto?> GetJobBotContextAsync(Guid jobId)
    {
        const string sql = @"
            SELECT
                org_id AS OrgId,
                language AS Language
            FROM jobs
            WHERE id = @jobId;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<JobBotContextDto>(sql, new { jobId });
    }
}

