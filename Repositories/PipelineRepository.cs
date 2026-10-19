using System.Data;
using System.Data.SqlClient;
using Dapper;
using nexthire_api.Data;
using nexthire_api.DTOs;
using nexthire_api.Exceptions;

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

    public async Task<IReadOnlyList<PipelineStageSettingsDto>> GetStageSettingsAsync(Guid orgId)
    {
        const string sql = @"
            SELECT
                ps.id AS Id,
                ps.name AS Name,
                ps.sort_order AS SortOrder,
                CAST(ps.is_system AS bit) AS IsSystem,
                (
                    SELECT COUNT(*)
                    FROM applications a
                    WHERE a.current_stage_id = ps.id
                      AND a.org_id = ps.org_id
                ) AS ApplicationCount,
                (
                    SELECT COUNT(*)
                    FROM stage_history sh
                    WHERE sh.from_stage_id = ps.id
                       OR sh.to_stage_id = ps.id
                ) AS HistoryCount
            FROM pipeline_stages ps
            WHERE ps.org_id = @orgId
            ORDER BY ps.sort_order ASC, ps.name ASC;";

        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<PipelineStageSettingsDto>(sql, new { orgId });
        return rows.ToList();
    }

    public async Task CreateStageAsync(Guid orgId, string name)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();

        await LockStagesAsync(connection, tx, orgId);
        await EnsureNameAvailableAsync(connection, tx, orgId, name, null);

        var sortOrder = await connection.ExecuteScalarAsync<int>(@"
            SELECT ISNULL(MAX(sort_order), 0) + 1
            FROM pipeline_stages
            WHERE org_id = @orgId;", new { orgId }, tx);

        try
        {
            await connection.ExecuteAsync(@"
                INSERT INTO pipeline_stages (id, org_id, name, sort_order, is_system, created_at)
                VALUES (@id, @orgId, @name, @sortOrder, 0, SYSDATETIMEOFFSET());",
                new { id = Guid.NewGuid(), orgId, name, sortOrder }, tx);
            tx.Commit();
        }
        catch (SqlException ex) when (IsUniqueViolation(ex))
        {
            throw DuplicateName();
        }
    }

    public async Task RenameStageAsync(Guid orgId, Guid stageId, string name)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();

        await LockStagesAsync(connection, tx, orgId);
        if (!await StageExistsAsync(connection, tx, orgId, stageId))
            throw new PipelineStageException(404, "not_found", "Column not found.");

        await EnsureNameAvailableAsync(connection, tx, orgId, name, stageId);

        try
        {
            await connection.ExecuteAsync(@"
                UPDATE pipeline_stages
                SET name = @name
                WHERE org_id = @orgId AND id = @stageId;",
                new { orgId, stageId, name }, tx);
            tx.Commit();
        }
        catch (SqlException ex) when (IsUniqueViolation(ex))
        {
            throw DuplicateName();
        }
    }

    public async Task ReorderStagesAsync(Guid orgId, IReadOnlyList<Guid> stageIds)
    {
        if (stageIds.Count == 0 || stageIds.Distinct().Count() != stageIds.Count)
            throw new PipelineStageException(400, "order_mismatch", "Column order must include every column exactly once.");

        using var connection = Open();
        using var tx = connection.BeginTransaction();

        var existing = (await connection.QueryAsync<Guid>(@"
            SELECT id
            FROM pipeline_stages WITH (UPDLOCK, HOLDLOCK)
            WHERE org_id = @orgId;", new { orgId }, tx)).ToHashSet();

        if (existing.Count != stageIds.Count || stageIds.Any(id => !existing.Contains(id)))
            throw new PipelineStageException(400, "order_mismatch", "Column order must include every column exactly once.");

        await connection.ExecuteAsync(@"
            ;WITH ranked AS (
                SELECT id, ROW_NUMBER() OVER (ORDER BY sort_order, id) AS rn
                FROM pipeline_stages
                WHERE org_id = @orgId
            )
            UPDATE ps
            SET sort_order = -ranked.rn
            FROM pipeline_stages ps
            INNER JOIN ranked ON ranked.id = ps.id;",
            new { orgId }, tx);

        for (var i = 0; i < stageIds.Count; i++)
        {
            await connection.ExecuteAsync(@"
                UPDATE pipeline_stages
                SET sort_order = @sortOrder
                WHERE org_id = @orgId AND id = @id;",
                new { orgId, id = stageIds[i], sortOrder = i + 1 }, tx);
        }

        tx.Commit();
    }

    public async Task DeleteStageAsync(Guid orgId, Guid stageId)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();

        var stageCount = await LockStagesAsync(connection, tx, orgId);
        if (!await StageExistsAsync(connection, tx, orgId, stageId))
            throw new PipelineStageException(404, "not_found", "Column not found.");
        if (stageCount <= 1)
            throw new PipelineStageException(400, "last_stage", "The organization must keep at least one column.");

        var applicationCount = await connection.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*)
            FROM applications
            WHERE org_id = @orgId AND current_stage_id = @stageId;",
            new { orgId, stageId }, tx);
        if (applicationCount > 0)
            throw new PipelineStageException(409, "in_use", "This column has candidates and cannot be deleted.");

        var historyCount = await connection.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*)
            FROM stage_history
            WHERE from_stage_id = @stageId OR to_stage_id = @stageId;",
            new { stageId }, tx);
        if (historyCount > 0)
            throw new PipelineStageException(409, "history", "This column has movement history and cannot be deleted.");

        try
        {
            var deleted = await connection.ExecuteAsync(@"
                DELETE FROM pipeline_stages
                WHERE org_id = @orgId AND id = @stageId;",
                new { orgId, stageId }, tx);
            if (deleted == 0)
                throw new PipelineStageException(404, "not_found", "Column not found.");
            tx.Commit();
        }
        catch (SqlException ex) when (ex.Number == 547)
        {
            throw new PipelineStageException(409, "in_use", "This column is still in use and cannot be deleted.");
        }
    }

    private IDbConnection Open()
    {
        var connection = _connectionFactory.CreateConnection();
        connection.Open();
        return connection;
    }

    private static async Task<int> LockStagesAsync(IDbConnection connection, IDbTransaction tx, Guid orgId)
    {
        return await connection.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*)
            FROM pipeline_stages WITH (UPDLOCK, HOLDLOCK)
            WHERE org_id = @orgId;", new { orgId }, tx);
    }

    private static async Task<bool> StageExistsAsync(IDbConnection connection, IDbTransaction tx, Guid orgId, Guid stageId)
    {
        var exists = await connection.ExecuteScalarAsync<int>(@"
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM pipeline_stages WHERE org_id = @orgId AND id = @stageId
            ) THEN 1 ELSE 0 END;", new { orgId, stageId }, tx);
        return exists == 1;
    }

    private static async Task EnsureNameAvailableAsync(
        IDbConnection connection,
        IDbTransaction tx,
        Guid orgId,
        string name,
        Guid? excludeStageId)
    {
        var taken = await connection.ExecuteScalarAsync<int>(@"
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM pipeline_stages
                WHERE org_id = @orgId
                  AND name = @name
                  AND (@excludeStageId IS NULL OR id <> @excludeStageId)
            ) THEN 1 ELSE 0 END;",
            new { orgId, name, excludeStageId }, tx);
        if (taken == 1)
            throw DuplicateName();
    }

    private static PipelineStageException DuplicateName()
        => new(409, "duplicate_name", "A column with that name already exists.");

    private static bool IsUniqueViolation(SqlException ex)
        => ex.Number is 2601 or 2627;
}

