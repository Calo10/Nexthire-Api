using Dapper;
using nexthire_api.Data;
using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public class JobRepository : IJobRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<JobRepository> _logger;

    public JobRepository(IDbConnectionFactory connectionFactory, ILogger<JobRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<IEnumerable<JobDto>> GetAllAsync(Guid orgId)
    {
        const string sql = @"
            SELECT 
                j.id AS Id,
                j.title AS Title,
                j.department AS Department,
                j.location AS Location,
                j.description AS Description,
                j.status AS Status,
                j.created_at AS CreatedAt,
                j.updated_at AS UpdatedAt,
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
        const string sql = @"
            SELECT 
                j.id AS Id,
                j.title AS Title,
                j.department AS Department,
                j.location AS Location,
                j.description AS Description,
                j.status AS Status,
                j.created_at AS CreatedAt,
                j.updated_at AS UpdatedAt,
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
        const string sql = @"
            SELECT 
                j.id AS Id,
                j.title AS Title,
                j.department AS Department,
                j.location AS Location,
                j.description AS Description,
                j.status AS Status,
                j.created_at AS CreatedAt,
                j.updated_at AS UpdatedAt,
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
        const string sql = @"
            SELECT 
                j.id AS Id,
                j.title AS Title,
                j.department AS Department,
                j.location AS Location,
                j.description AS Description,
                j.status AS Status,
                j.created_at AS CreatedAt,
                j.updated_at AS UpdatedAt,
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
        const string sql = @"
            INSERT INTO jobs (id, org_id, created_by_user_id, title, status, department, location, description, created_at, updated_at)
            OUTPUT INSERTED.id AS Id,
                   INSERTED.title AS Title,
                   INSERTED.department AS Department,
                   INSERTED.location AS Location,
                   INSERTED.description AS Description,
                   INSERTED.status AS Status,
                   INSERTED.created_at AS CreatedAt,
                   INSERTED.updated_at AS UpdatedAt
            VALUES (NEWID(), @OrgId, @CreatedByUserId, @Title, COALESCE(@Status, 'Open'), @Department, @Location, @Description, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QuerySingleAsync<JobDto>(sql, new
        {
            OrgId = orgId,
            CreatedByUserId = createdByUserId,
            dto.Title,
            dto.Status,
            dto.Department,
            dto.Location,
            dto.Description
        });
    }

    public async Task<JobDto?> UpdateAsync(Guid orgId, Guid id, UpdateJobDto dto)
    {
        const string sql = @"
            UPDATE jobs
            SET
                title = COALESCE(@Title, title),
                department = COALESCE(@Department, department),
                location = COALESCE(@Location, location),
                description = COALESCE(@Description, description),
                status = COALESCE(@Status, status),
                updated_at = SYSDATETIMEOFFSET()
            WHERE org_id = @OrgId AND id = @Id;

            SELECT 
                j.id AS Id,
                j.title AS Title,
                j.department AS Department,
                j.location AS Location,
                j.description AS Description,
                j.status AS Status,
                j.created_at AS CreatedAt,
                j.updated_at AS UpdatedAt,
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
                dto.Status
            });

        return result;
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

