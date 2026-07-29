using Dapper;
using nexthire_api.Data;
using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public class JobRepository : IJobRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<JobRepository> _logger;
    private static int _adDesignSchemaEnsured;

    private const string JobSelectColumns = @"
                j.id AS Id,
                j.title AS Title,
                j.department AS Department,
                j.location AS Location,
                j.description AS Description,
                j.status AS Status,
                j.language AS Language,
                j.created_at AS CreatedAt,
                j.updated_at AS UpdatedAt,
                CASE
                    WHEN j.ad_design_base64 IS NOT NULL AND LTRIM(RTRIM(j.ad_design_base64)) <> '' THEN CAST(1 AS bit)
                    ELSE CAST(0 AS bit)
                END AS HasAdDesign";

    public JobRepository(IDbConnectionFactory connectionFactory, ILogger<JobRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task EnsureAdDesignSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _adDesignSchemaEnsured, 1, 0) == 1)
            return;

        const string sql = @"
IF COL_LENGTH('dbo.jobs', 'ad_design_base64') IS NULL
BEGIN
    ALTER TABLE dbo.jobs ADD ad_design_base64 NVARCHAR(MAX) NULL;
END

IF COL_LENGTH('dbo.jobs', 'ad_design_content_type') IS NULL
BEGIN
    ALTER TABLE dbo.jobs ADD ad_design_content_type NVARCHAR(100) NULL;
END

IF COL_LENGTH('dbo.jobs', 'ad_design_text') IS NULL
BEGIN
    ALTER TABLE dbo.jobs ADD ad_design_text NVARCHAR(MAX) NULL;
END";

        try
        {
            using var connection = _connectionFactory.CreateConnection();
            await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
        }
        catch
        {
            Interlocked.Exchange(ref _adDesignSchemaEnsured, 0);
            throw;
        }
    }

    public async Task<IEnumerable<JobDto>> GetAllAsync(Guid orgId)
    {
        await EnsureAdDesignSchemaAsync();

        var sql = $@"
            SELECT 
                {JobSelectColumns},
                (
                    SELECT COUNT(DISTINCT a.candidate_id)
                    FROM applications a
                    WHERE a.org_id = @orgId AND a.job_id = j.id
                ) AS ApplicantsCount
            FROM jobs j
            WHERE j.org_id = @orgId
            ORDER BY j.created_at DESC;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryAsync<JobDto>(sql, new { orgId });
    }

    public async Task<JobDto?> GetByIdAsync(Guid orgId, Guid id)
    {
        await EnsureAdDesignSchemaAsync();

        var sql = $@"
            SELECT 
                {JobSelectColumns},
                (
                    SELECT COUNT(DISTINCT a.candidate_id)
                    FROM applications a
                    WHERE a.org_id = @orgId AND a.job_id = j.id
                ) AS ApplicantsCount
            FROM jobs j
            WHERE j.org_id = @orgId AND j.id = @id;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<JobDto>(sql, new { orgId, id });
    }

    public async Task<IEnumerable<JobDto>> GetPublicOpenAsync(Guid orgId)
    {
        await EnsureAdDesignSchemaAsync();

        var sql = $@"
            SELECT 
                {JobSelectColumns},
                (
                    SELECT COUNT(DISTINCT a.candidate_id)
                    FROM applications a
                    WHERE a.org_id = @orgId AND a.job_id = j.id
                ) AS ApplicantsCount
            FROM jobs j
            WHERE j.org_id = @orgId AND LOWER(j.status) = 'open'
            ORDER BY j.created_at DESC;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryAsync<JobDto>(sql, new { orgId });
    }

    public async Task<JobDto?> GetPublicOpenByIdAsync(Guid orgId, Guid id)
    {
        await EnsureAdDesignSchemaAsync();

        var sql = $@"
            SELECT 
                {JobSelectColumns},
                (
                    SELECT COUNT(DISTINCT a.candidate_id)
                    FROM applications a
                    WHERE a.org_id = @orgId AND a.job_id = j.id
                ) AS ApplicantsCount
            FROM jobs j
            WHERE j.org_id = @orgId AND j.id = @id AND LOWER(j.status) = 'open';";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<JobDto>(sql, new { orgId, id });
    }

    public async Task<JobDto> CreateAsync(Guid orgId, Guid createdByUserId, CreateJobDto dto)
    {
        await EnsureAdDesignSchemaAsync();

        const string sql = @"
            INSERT INTO jobs (id, org_id, created_by_user_id, title, status, department, location, description, language, created_at, updated_at)
            OUTPUT INSERTED.id AS Id,
                   INSERTED.title AS Title,
                   INSERTED.department AS Department,
                   INSERTED.location AS Location,
                   INSERTED.description AS Description,
                   INSERTED.status AS Status,
                   INSERTED.language AS Language,
                   INSERTED.created_at AS CreatedAt,
                   INSERTED.updated_at AS UpdatedAt,
                   CAST(0 AS bit) AS HasAdDesign
            VALUES (NEWID(), @OrgId, @CreatedByUserId, @Title, COALESCE(@Status, 'Open'), @Department, @Location, @Description, @Language, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QuerySingleAsync<JobDto>(sql, new
        {
            OrgId = orgId,
            CreatedByUserId = createdByUserId,
            dto.Title,
            dto.Status,
            dto.Department,
            dto.Location,
            dto.Description,
            Language = JobLanguageCodes.NormalizeOrDefault(dto.Language)
        });
    }

    public async Task<JobDto?> UpdateAsync(Guid orgId, Guid id, UpdateJobDto dto)
    {
        await EnsureAdDesignSchemaAsync();

        var sql = $@"
            UPDATE jobs
            SET
                title = COALESCE(@Title, title),
                department = COALESCE(@Department, department),
                location = COALESCE(@Location, location),
                description = COALESCE(@Description, description),
                status = COALESCE(@Status, status),
                language = COALESCE(@Language, language),
                updated_at = SYSDATETIMEOFFSET()
            WHERE org_id = @OrgId AND id = @Id;

            SELECT 
                {JobSelectColumns},
                (
                    SELECT COUNT(DISTINCT a.candidate_id)
                    FROM applications a
                    WHERE a.org_id = @OrgId AND a.job_id = j.id
                ) AS ApplicantsCount
            FROM jobs j
            WHERE j.org_id = @OrgId AND j.id = @Id;";

        using var connection = _connectionFactory.CreateConnection();

        var result = await connection.QueryFirstOrDefaultAsync<JobDto>(
            sql,
            new
            {
                OrgId = orgId,
                Id = id,
                dto.Title,
                dto.Department,
                dto.Location,
                dto.Description,
                dto.Status,
                Language = string.IsNullOrWhiteSpace(dto.Language)
                    ? null
                    : JobLanguageCodes.NormalizeOrDefault(dto.Language)
            });

        return result;
    }

    public async Task<JobAdDesignDto?> GetAdDesignAsync(
        Guid orgId,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAdDesignSchemaAsync(cancellationToken);

        const string sql = @"
SELECT
    id AS JobId,
    ad_design_base64 AS ImageBase64,
    ad_design_content_type AS ImageContentType,
    ad_design_text AS AdText
FROM dbo.jobs
WHERE org_id = @orgId
  AND id = @jobId
  AND ad_design_base64 IS NOT NULL
  AND LTRIM(RTRIM(ad_design_base64)) <> '';";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<JobAdDesignDto>(
            new CommandDefinition(sql, new { orgId, jobId }, cancellationToken: cancellationToken));
    }

    public async Task<JobAdDesignDto?> SaveAdDesignAsync(
        Guid orgId,
        Guid jobId,
        SaveJobAdDesignRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureAdDesignSchemaAsync(cancellationToken);

        const string sql = @"
UPDATE dbo.jobs
SET
    ad_design_base64 = @ImageBase64,
    ad_design_content_type = @ImageContentType,
    ad_design_text = @AdText,
    updated_at = SYSDATETIMEOFFSET()
WHERE org_id = @orgId AND id = @jobId;

SELECT
    id AS JobId,
    ad_design_base64 AS ImageBase64,
    ad_design_content_type AS ImageContentType,
    ad_design_text AS AdText
FROM dbo.jobs
WHERE org_id = @orgId AND id = @jobId;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<JobAdDesignDto>(
            new CommandDefinition(
                sql,
                new
                {
                    orgId,
                    jobId,
                    ImageBase64 = request.ImageBase64,
                    ImageContentType = string.IsNullOrWhiteSpace(request.ImageContentType)
                        ? "image/png"
                        : request.ImageContentType.Trim(),
                    AdText = string.IsNullOrWhiteSpace(request.AdText) ? null : request.AdText.Trim()
                },
                cancellationToken: cancellationToken));
    }

    public async Task<bool> DeleteAdDesignAsync(
        Guid orgId,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAdDesignSchemaAsync(cancellationToken);

        const string sql = @"
UPDATE dbo.jobs
SET
    ad_design_base64 = NULL,
    ad_design_content_type = NULL,
    ad_design_text = NULL,
    updated_at = SYSDATETIMEOFFSET()
WHERE org_id = @orgId AND id = @jobId;";

        using var connection = _connectionFactory.CreateConnection();
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(sql, new { orgId, jobId }, cancellationToken: cancellationToken));
        return affected > 0;
    }

    public async Task<bool> HasApplicationsAsync(Guid orgId, Guid jobId)
    {
        const string sql = @"
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM applications a
                WHERE a.org_id = @orgId AND a.job_id = @jobId
            ) THEN 1 ELSE 0 END;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<int>(sql, new { orgId, jobId }) == 1;
    }

    public async Task<bool> DeleteAsync(Guid orgId, Guid id)
    {
        const string sql = @"DELETE FROM jobs WHERE org_id = @orgId AND id = @id;";
        using var connection = _connectionFactory.CreateConnection();
        var affected = await connection.ExecuteAsync(sql, new { orgId, id });
        return affected > 0;
    }
}
