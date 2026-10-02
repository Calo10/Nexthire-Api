using System.Data;
using System.Data.SqlClient;
using Dapper;
using nexthire_api.Data;
using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public class ApplicationsRepository : IApplicationsRepository
{
    /// <summary>
    /// One pass over sourcing_leads for the org (or job), then a join.
    /// The previous OUTER APPLY scanned leads once per application and compared emails with LOWER, which cannot use an index.
    /// </summary>
    private const string FitScoreJoin = @"
            LEFT JOIN (
                SELECT ranked.application_id, ranked.fit_score
                FROM (
                    SELECT
                        match.application_id,
                        match.fit_score,
                        ROW_NUMBER() OVER (
                            PARTITION BY match.application_id
                            ORDER BY match.priority, match.updated_at DESC
                        ) AS rn
                    FROM (
                        SELECT
                            sl.converted_application_id AS application_id,
                            sl.fit_score,
                            sl.updated_at,
                            0 AS priority
                        FROM sourcing_leads sl
                        WHERE sl.org_id = @orgId
                          AND sl.converted_application_id IS NOT NULL
                          AND (@jobId IS NULL OR sl.job_id = @jobId)

                        UNION ALL

                        SELECT
                            a2.id,
                            sl.fit_score,
                            sl.updated_at,
                            1
                        FROM sourcing_leads sl
                        INNER JOIN applications a2
                            ON a2.org_id = sl.org_id
                           AND a2.job_id = sl.job_id
                           AND a2.candidate_id = sl.converted_candidate_id
                        WHERE sl.org_id = @orgId
                          AND sl.converted_candidate_id IS NOT NULL
                          AND (@jobId IS NULL OR sl.job_id = @jobId)

                        UNION ALL

                        SELECT
                            a2.id,
                            sl.fit_score,
                            sl.updated_at,
                            2
                        FROM sourcing_leads sl
                        INNER JOIN candidates c2
                            ON c2.org_id = sl.org_id
                           AND c2.email = sl.email
                        INNER JOIN applications a2
                            ON a2.org_id = sl.org_id
                           AND a2.job_id = sl.job_id
                           AND a2.candidate_id = c2.id
                        WHERE sl.org_id = @orgId
                          AND sl.email IS NOT NULL
                          AND sl.converted_application_id IS NULL
                          AND sl.converted_candidate_id IS NULL
                          AND (@jobId IS NULL OR sl.job_id = @jobId)
                    ) match
                ) ranked
                WHERE ranked.rn = 1
            ) leadFit ON leadFit.application_id = a.id";

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IUserRepository _users;
    private readonly ILogger<ApplicationsRepository> _logger;

    public ApplicationsRepository(
        IDbConnectionFactory connectionFactory,
        IUserRepository users,
        ILogger<ApplicationsRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _users = users;
        _logger = logger;
    }

    public async Task<PagedResult<ApplicationListItemDto>> GetPagedAsync(
        Guid orgId,
        Guid? jobId,
        Guid? candidateId,
        Guid? stageId,
        string? status,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? search,
        int page,
        int pageSize)
    {
        var offset = (page - 1) * pageSize;
        var trimmedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var trimmedStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim();

        const string sql = @"
            SELECT COUNT(1)
            FROM applications a
            INNER JOIN jobs j ON j.id = a.job_id AND j.org_id = @orgId
            INNER JOIN candidates c ON c.id = a.candidate_id AND c.org_id = @orgId
            INNER JOIN pipeline_stages ps ON ps.id = a.current_stage_id AND ps.org_id = @orgId
            WHERE a.org_id = @orgId
              AND (@jobId IS NULL OR a.job_id = @jobId)
              AND (@candidateId IS NULL OR a.candidate_id = @candidateId)
              AND (@stageId IS NULL OR a.current_stage_id = @stageId)
              AND (@status IS NULL OR a.status = @status)
              AND (@from IS NULL OR COALESCE(a.applied_at, a.created_at) >= @from)
              AND (@to IS NULL OR COALESCE(a.applied_at, a.created_at) <= @to)
              AND (
                    @search IS NULL
                    OR LOWER(j.title) LIKE '%' + LOWER(@search) + '%'
                    OR LOWER(c.first_name) LIKE '%' + LOWER(@search) + '%'
                    OR LOWER(c.last_name) LIKE '%' + LOWER(@search) + '%'
                    OR LOWER(c.email) LIKE '%' + LOWER(@search) + '%'
                  );

            SELECT
                a.id AS ApplicationId,
                a.job_id AS JobId,
                j.title AS JobTitle,
                a.candidate_id AS CandidateId,
                (c.first_name + ' ' + c.last_name) AS CandidateName,
                c.email AS CandidateEmail,
                a.current_stage_id AS CurrentStageId,
                ps.name AS CurrentStageName,
                a.status AS Status,
                a.applied_at AS AppliedAt,
                a.created_at AS CreatedAt,
                a.updated_at AS UpdatedAt
            FROM applications a
            INNER JOIN jobs j ON j.id = a.job_id AND j.org_id = @orgId
            INNER JOIN candidates c ON c.id = a.candidate_id AND c.org_id = @orgId
            INNER JOIN pipeline_stages ps ON ps.id = a.current_stage_id AND ps.org_id = @orgId
            WHERE a.org_id = @orgId
              AND (@jobId IS NULL OR a.job_id = @jobId)
              AND (@candidateId IS NULL OR a.candidate_id = @candidateId)
              AND (@stageId IS NULL OR a.current_stage_id = @stageId)
              AND (@status IS NULL OR a.status = @status)
              AND (@from IS NULL OR COALESCE(a.applied_at, a.created_at) >= @from)
              AND (@to IS NULL OR COALESCE(a.applied_at, a.created_at) <= @to)
              AND (
                    @search IS NULL
                    OR LOWER(j.title) LIKE '%' + LOWER(@search) + '%'
                    OR LOWER(c.first_name) LIKE '%' + LOWER(@search) + '%'
                    OR LOWER(c.last_name) LIKE '%' + LOWER(@search) + '%'
                    OR LOWER(c.email) LIKE '%' + LOWER(@search) + '%'
                  )
            ORDER BY COALESCE(a.applied_at, a.created_at) DESC, a.updated_at DESC
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;";

        using var connection = _connectionFactory.CreateConnection();
        using var multi = await connection.QueryMultipleAsync(sql, new
        {
            orgId,
            jobId,
            candidateId,
            stageId,
            status = trimmedStatus,
            from,
            to,
            search = trimmedSearch,
            offset,
            pageSize
        });

        var total = await multi.ReadSingleAsync<int>();
        var items = (await multi.ReadAsync<ApplicationListItemDto>()).ToList();

        return new PagedResult<ApplicationListItemDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            Total = total
        };
    }

    public async Task<ApplicationListItemDto?> GetByIdAsync(Guid orgId, Guid id)
    {
        const string sql = @"
            SELECT
                a.id AS ApplicationId,
                a.job_id AS JobId,
                j.title AS JobTitle,
                a.candidate_id AS CandidateId,
                (c.first_name + ' ' + c.last_name) AS CandidateName,
                c.email AS CandidateEmail,
                a.current_stage_id AS CurrentStageId,
                ps.name AS CurrentStageName,
                a.status AS Status,
                a.applied_at AS AppliedAt,
                a.created_at AS CreatedAt,
                a.updated_at AS UpdatedAt
            FROM applications a
            INNER JOIN jobs j ON j.id = a.job_id AND j.org_id = @orgId
            INNER JOIN candidates c ON c.id = a.candidate_id AND c.org_id = @orgId
            INNER JOIN pipeline_stages ps ON ps.id = a.current_stage_id AND ps.org_id = @orgId
            WHERE a.org_id = @orgId AND a.id = @id;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<ApplicationListItemDto>(sql, new { orgId, id });
    }

    public async Task<IReadOnlyList<ApplicationStageHistoryItemDto>> GetStageHistoryAsync(Guid orgId, Guid applicationId, int limit)
    {
        // Assumed schema: stage_history(id, application_id, from_stage_id, to_stage_id, moved_by_user_id, moved_at)
        const string sql = @"
            SELECT TOP (@limit)
                sh.id AS Id,
                sh.application_id AS ApplicationId,
                sh.from_stage_id AS FromStageId,
                psFrom.name AS FromStageName,
                sh.to_stage_id AS ToStageId,
                psTo.name AS ToStageName,
                sh.moved_by_user_id AS MovedByUserId,
                COALESCE(u.display_name, u.email) AS MovedByName,
                u.email AS MovedByEmail,
                sh.moved_at AS MovedAt
            FROM stage_history sh
            INNER JOIN applications a ON a.id = sh.application_id AND a.org_id = @orgId
            LEFT JOIN pipeline_stages psFrom ON psFrom.id = sh.from_stage_id AND psFrom.org_id = @orgId
            INNER JOIN pipeline_stages psTo ON psTo.id = sh.to_stage_id AND psTo.org_id = @orgId
            LEFT JOIN nh_users u ON u.id = sh.moved_by_user_id AND u.org_id = @orgId
            WHERE sh.application_id = @applicationId
            ORDER BY sh.moved_at DESC;";

        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<ApplicationStageHistoryItemDto>(sql, new { orgId, applicationId, limit });
        return rows.ToList();
    }

    public async Task<bool> JobExistsInOrgAsync(Guid orgId, Guid jobId)
    {
        const string sql = @"SELECT CASE WHEN EXISTS (SELECT 1 FROM jobs WHERE org_id = @orgId AND id = @jobId) THEN 1 ELSE 0 END;";
        using var connection = _connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<int>(sql, new { orgId, jobId }) == 1;
    }

    public async Task<bool> CandidateExistsInOrgAsync(Guid orgId, Guid candidateId)
    {
        const string sql = @"SELECT CASE WHEN EXISTS (SELECT 1 FROM candidates WHERE org_id = @orgId AND id = @candidateId) THEN 1 ELSE 0 END;";
        using var connection = _connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<int>(sql, new { orgId, candidateId }) == 1;
    }

    public async Task<bool> StageExistsInOrgAsync(Guid orgId, Guid stageId)
    {
        const string sql = @"SELECT CASE WHEN EXISTS (SELECT 1 FROM pipeline_stages WHERE org_id = @orgId AND id = @stageId) THEN 1 ELSE 0 END;";
        using var connection = _connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<int>(sql, new { orgId, stageId }) == 1;
    }

    public async Task<Guid> GetOrCreateNhUserIdAsync(Guid orgId, Guid nexaUserId, string? email, string? displayName)
    {
        var safeEmail = string.IsNullOrWhiteSpace(email)
            ? $"{nexaUserId}@unknown.local"
            : email.Trim();

        return await _users.UpsertFromNexaMemberAsync(orgId, nexaUserId, safeEmail, displayName);
    }

    public async Task<ApplicationListItemDto> CreateAsync(Guid orgId, CreateApplicationRequestDto request)
    {
        const string sql = @"
            INSERT INTO applications (id, org_id, job_id, candidate_id, current_stage_id, status, applied_at, created_at, updated_at)
            VALUES (
                @Id,
                @OrgId,
                @JobId,
                @CandidateId,
                @CurrentStageId,
                @Status,
                @AppliedAt,
                TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'),
                TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            );

            SELECT
                a.id AS ApplicationId,
                a.job_id AS JobId,
                j.title AS JobTitle,
                a.candidate_id AS CandidateId,
                (c.first_name + ' ' + c.last_name) AS CandidateName,
                c.email AS CandidateEmail,
                a.current_stage_id AS CurrentStageId,
                ps.name AS CurrentStageName,
                a.status AS Status,
                a.applied_at AS AppliedAt,
                a.created_at AS CreatedAt,
                a.updated_at AS UpdatedAt
            FROM applications a
            INNER JOIN jobs j ON j.id = a.job_id AND j.org_id = @OrgId
            INNER JOIN candidates c ON c.id = a.candidate_id AND c.org_id = @OrgId
            INNER JOIN pipeline_stages ps ON ps.id = a.current_stage_id AND ps.org_id = @OrgId
            WHERE a.org_id = @OrgId AND a.id = @Id;";

        using var connection = _connectionFactory.CreateConnection();
        var id = Guid.NewGuid();
        return await connection.QuerySingleAsync<ApplicationListItemDto>(sql, new
        {
            Id = id,
            OrgId = orgId,
            request.JobId,
            request.CandidateId,
            request.CurrentStageId,
            request.Status,
            request.AppliedAt
        });
    }

    public async Task<(ApplicationListItemDto? Updated, Guid? PreviousStageId)> UpdateAsync(Guid orgId, Guid applicationId, UpdateApplicationRequestDto request)
    {
        // Fetch previous stage for stage history logic.
        const string prevSql = @"SELECT current_stage_id FROM applications WHERE org_id = @orgId AND id = @applicationId;";

        using var connection = _connectionFactory.CreateConnection();
        var prevStage = await connection.ExecuteScalarAsync<Guid?>(prevSql, new { orgId, applicationId });
        if (!prevStage.HasValue)
            return (null, null);

        const string sql = @"
            UPDATE applications
            SET
                status = COALESCE(@Status, status),
                current_stage_id = COALESCE(@CurrentStageId, current_stage_id),
                applied_at = @AppliedAt,
                updated_at = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            WHERE org_id = @OrgId AND id = @Id;

            SELECT
                a.id AS ApplicationId,
                a.job_id AS JobId,
                j.title AS JobTitle,
                a.candidate_id AS CandidateId,
                (c.first_name + ' ' + c.last_name) AS CandidateName,
                c.email AS CandidateEmail,
                a.current_stage_id AS CurrentStageId,
                ps.name AS CurrentStageName,
                a.status AS Status,
                a.applied_at AS AppliedAt,
                a.created_at AS CreatedAt,
                a.updated_at AS UpdatedAt
            FROM applications a
            INNER JOIN jobs j ON j.id = a.job_id AND j.org_id = @OrgId
            INNER JOIN candidates c ON c.id = a.candidate_id AND c.org_id = @OrgId
            INNER JOIN pipeline_stages ps ON ps.id = a.current_stage_id AND ps.org_id = @OrgId
            WHERE a.org_id = @OrgId AND a.id = @Id;";

        var updated = await connection.QueryFirstOrDefaultAsync<ApplicationListItemDto>(sql, new
        {
            OrgId = orgId,
            Id = applicationId,
            request.Status,
            request.CurrentStageId,
            AppliedAt = request.AppliedAt // note: this will set to null if null is passed (intentional)
        });

        return (updated, prevStage.Value);
    }

    public async Task InsertStageHistoryAsync(Guid orgId, Guid applicationId, Guid? fromStageId, Guid toStageId, Guid movedByUserId, IDbTransaction? transaction = null)
    {
        const string sql = @"
            INSERT INTO stage_history (id, application_id, from_stage_id, to_stage_id, moved_by_user_id, moved_at)
            VALUES (@id, @applicationId, @fromStageId, @toStageId, @movedByUserId, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'));";

        var connection = transaction?.Connection ?? _connectionFactory.CreateConnection();
        var ownsConnection = transaction == null;
        try
        {
            if (ownsConnection)
                connection.Open();
            else if (connection.State != ConnectionState.Open)
                connection.Open();

            await connection.ExecuteAsync(sql, new
            {
                id = Guid.NewGuid(),
                applicationId,
                fromStageId,
                toStageId,
                movedByUserId
            }, transaction);
        }
        finally
        {
            if (ownsConnection)
                connection.Dispose();
        }
    }

    public async Task<ApplicationListItemDto?> SoftArchiveAsync(Guid orgId, Guid applicationId)
    {
        const string sql = @"
            UPDATE applications
            SET
                status = 'archived',
                updated_at = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            WHERE org_id = @orgId AND id = @applicationId;

            SELECT
                a.id AS ApplicationId,
                a.job_id AS JobId,
                j.title AS JobTitle,
                a.candidate_id AS CandidateId,
                (c.first_name + ' ' + c.last_name) AS CandidateName,
                c.email AS CandidateEmail,
                a.current_stage_id AS CurrentStageId,
                ps.name AS CurrentStageName,
                a.status AS Status,
                a.applied_at AS AppliedAt,
                a.created_at AS CreatedAt,
                a.updated_at AS UpdatedAt
            FROM applications a
            INNER JOIN jobs j ON j.id = a.job_id AND j.org_id = @orgId
            INNER JOIN candidates c ON c.id = a.candidate_id AND c.org_id = @orgId
            INNER JOIN pipeline_stages ps ON ps.id = a.current_stage_id AND ps.org_id = @orgId
            WHERE a.org_id = @orgId AND a.id = @applicationId;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<ApplicationListItemDto>(sql, new { orgId, applicationId });
    }

    public async Task<IReadOnlyList<ApplicationCardDto>> GetKanbanCardsAsync(Guid orgId, Guid? jobId)
    {
        var sql = @"
            SELECT
                a.id AS Id,
                a.candidate_id AS CandidateId,
                (c.first_name + ' ' + c.last_name) AS CandidateName,
                a.job_id AS JobId,
                j.title AS JobTitle,
                a.current_stage_id AS CurrentStageId,
                a.status AS Status,
                a.applied_at AS AppliedAt,
                a.created_at AS CreatedAt,
                leadFit.fit_score AS FitScore
            FROM applications a
            INNER JOIN jobs j ON j.id = a.job_id AND j.org_id = @orgId
            INNER JOIN candidates c ON c.id = a.candidate_id AND c.org_id = @orgId
            " + FitScoreJoin + @"
            WHERE a.org_id = @orgId";

        if (jobId is Guid id && id != Guid.Empty)
            sql += " AND a.job_id = @jobId";

        sql += " ORDER BY a.created_at DESC;";

        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<ApplicationCardDto>(sql, new { orgId, jobId });
        return rows.ToList();
    }

    public async Task<ApplicationCardDto?> GetKanbanCardByIdAsync(Guid orgId, Guid applicationId)
    {
        const string sql = @"
            SELECT
                a.id AS Id,
                a.candidate_id AS CandidateId,
                (c.first_name + ' ' + c.last_name) AS CandidateName,
                a.job_id AS JobId,
                j.title AS JobTitle,
                a.current_stage_id AS CurrentStageId,
                a.status AS Status,
                a.applied_at AS AppliedAt,
                a.created_at AS CreatedAt,
                leadFit.fit_score AS FitScore
            FROM applications a
            INNER JOIN jobs j ON j.id = a.job_id AND j.org_id = @orgId
            INNER JOIN candidates c ON c.id = a.candidate_id AND c.org_id = @orgId
            " + FitScoreJoin + @"
            WHERE a.org_id = @orgId AND a.id = @applicationId;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<ApplicationCardDto>(sql, new { orgId, applicationId, jobId = (Guid?)null });
    }

    public async Task<ApplicationCardDto> CreateKanbanAsync(Guid orgId, Guid jobId, Guid candidateId, Guid currentStageId, string status, IDbTransaction? transaction = null)
    {
        const string sql = @"
            INSERT INTO applications (id, org_id, job_id, candidate_id, current_stage_id, status, applied_at, created_at, updated_at)
            VALUES (
                @Id,
                @OrgId,
                @JobId,
                @CandidateId,
                @CurrentStageId,
                @Status,
                TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'),
                TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'),
                TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            );

            SELECT
                a.id AS Id,
                a.candidate_id AS CandidateId,
                (c.first_name + ' ' + c.last_name) AS CandidateName,
                a.job_id AS JobId,
                j.title AS JobTitle,
                a.current_stage_id AS CurrentStageId,
                a.status AS Status,
                a.applied_at AS AppliedAt,
                a.created_at AS CreatedAt,
                leadFit.fit_score AS FitScore
            FROM applications a
            INNER JOIN jobs j ON j.id = a.job_id AND j.org_id = @OrgId
            INNER JOIN candidates c ON c.id = a.candidate_id AND c.org_id = @OrgId
            " + FitScoreJoin + @"
            WHERE a.org_id = @OrgId AND a.id = @Id;";

        var connection = transaction?.Connection ?? _connectionFactory.CreateConnection();
        var ownsConnection = transaction == null;
        try
        {
            if (ownsConnection)
                connection.Open();
            else if (connection.State != ConnectionState.Open)
                connection.Open();

            var id = Guid.NewGuid();
            return await connection.QuerySingleAsync<ApplicationCardDto>(sql, new
            {
                Id = id,
                OrgId = orgId,
                orgId,
                jobId,
                JobId = jobId,
                CandidateId = candidateId,
                CurrentStageId = currentStageId,
                Status = status
            }, transaction);
        }
        finally
        {
            if (ownsConnection)
                connection.Dispose();
        }
    }

    public async Task<ApplicationCardDto?> MoveKanbanAsync(Guid orgId, Guid applicationId, Guid toStageId, Guid movedByUserId)
    {
        using var connection = _connectionFactory.CreateConnection();
        if (connection.State != ConnectionState.Open)
            connection.Open();

        using var tx = connection.BeginTransaction();
        try
        {
            const string prevSql = @"SELECT current_stage_id FROM applications WHERE org_id = @orgId AND id = @applicationId;";
            var fromStageId = await connection.ExecuteScalarAsync<Guid?>(prevSql, new { orgId, applicationId }, tx);
            if (!fromStageId.HasValue)
            {
                tx.Rollback();
                return null;
            }

            const string updateSql = @"
                UPDATE applications
                SET current_stage_id = @toStageId,
                    updated_at = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
                WHERE org_id = @orgId AND id = @applicationId;";

            var updated = await connection.ExecuteAsync(updateSql, new { orgId, applicationId, toStageId }, tx);
            if (updated <= 0)
            {
                tx.Rollback();
                return null;
            }

            const string insertHistorySql = @"
                INSERT INTO stage_history (id, application_id, from_stage_id, to_stage_id, moved_by_user_id, moved_at)
                VALUES (@id, @applicationId, @fromStageId, @toStageId, @movedByUserId, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'));";

            await connection.ExecuteAsync(insertHistorySql, new
            {
                id = Guid.NewGuid(),
                applicationId,
                fromStageId,
                toStageId,
                movedByUserId
            }, tx);

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        return await GetKanbanCardByIdAsync(orgId, applicationId);
    }

    public async Task<bool> ExistsForJobCandidateAsync(Guid orgId, Guid jobId, Guid candidateId, IDbTransaction? transaction = null)
    {
        const string sql = @"
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM applications a
                WHERE a.org_id = @orgId AND a.job_id = @jobId AND a.candidate_id = @candidateId
            ) THEN 1 ELSE 0 END;";

        var connection = transaction?.Connection ?? _connectionFactory.CreateConnection();
        var ownsConnection = transaction == null;
        try
        {
            if (ownsConnection)
                connection.Open();
            else if (connection.State != ConnectionState.Open)
                connection.Open();

            return await connection.ExecuteScalarAsync<int>(sql, new { orgId, jobId, candidateId }, transaction) == 1;
        }
        finally
        {
            if (ownsConnection)
                connection.Dispose();
        }
    }

    public async Task<Guid?> FindApplicationIdForJobCandidateAsync(Guid orgId, Guid jobId, Guid candidateId, IDbTransaction? transaction = null)
    {
        const string sql = @"
            SELECT TOP 1 a.id
            FROM applications a
            WHERE a.org_id = @orgId AND a.job_id = @jobId AND a.candidate_id = @candidateId;";

        var connection = transaction?.Connection ?? _connectionFactory.CreateConnection();
        var ownsConnection = transaction == null;
        try
        {
            if (ownsConnection)
                connection.Open();
            else if (connection.State != ConnectionState.Open)
                connection.Open();

            return await connection.ExecuteScalarAsync<Guid?>(sql, new { orgId, jobId, candidateId }, transaction);
        }
        finally
        {
            if (ownsConnection)
                connection.Dispose();
        }
    }

    public async Task EnsureListIndexesAsync()
    {
        const string sql = @"
IF OBJECT_ID(N'dbo.applications', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_applications_org_job_created' AND object_id = OBJECT_ID(N'dbo.applications'))
    CREATE INDEX IX_applications_org_job_created
        ON dbo.applications (org_id, job_id, created_at DESC)
        INCLUDE (candidate_id, current_stage_id, status, applied_at);

IF OBJECT_ID(N'dbo.applications', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_applications_org_candidate_updated' AND object_id = OBJECT_ID(N'dbo.applications'))
    CREATE INDEX IX_applications_org_candidate_updated
        ON dbo.applications (org_id, candidate_id, updated_at DESC)
        INCLUDE (current_stage_id, job_id);

IF OBJECT_ID(N'dbo.candidates', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_candidates_org_created' AND object_id = OBJECT_ID(N'dbo.candidates'))
    CREATE INDEX IX_candidates_org_created
        ON dbo.candidates (org_id, created_at DESC);

IF OBJECT_ID(N'dbo.candidates', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_candidates_org_email' AND object_id = OBJECT_ID(N'dbo.candidates'))
    CREATE INDEX IX_candidates_org_email
        ON dbo.candidates (org_id, email);

IF OBJECT_ID(N'dbo.sourcing_leads', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_sourcing_leads_org_application' AND object_id = OBJECT_ID(N'dbo.sourcing_leads'))
    CREATE INDEX IX_sourcing_leads_org_application
        ON dbo.sourcing_leads (org_id, converted_application_id)
        INCLUDE (job_id, fit_score, updated_at);

IF OBJECT_ID(N'dbo.sourcing_leads', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_sourcing_leads_org_candidate' AND object_id = OBJECT_ID(N'dbo.sourcing_leads'))
    CREATE INDEX IX_sourcing_leads_org_candidate
        ON dbo.sourcing_leads (org_id, converted_candidate_id, job_id)
        INCLUDE (fit_score, updated_at, converted_application_id);

IF OBJECT_ID(N'dbo.sourcing_leads', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_sourcing_leads_org_email' AND object_id = OBJECT_ID(N'dbo.sourcing_leads'))
    CREATE INDEX IX_sourcing_leads_org_email
        ON dbo.sourcing_leads (org_id, email)
        INCLUDE (job_id, fit_score, updated_at, converted_application_id, converted_candidate_id);

IF OBJECT_ID(N'dbo.sourcing_leads', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_sourcing_leads_org_created' AND object_id = OBJECT_ID(N'dbo.sourcing_leads'))
    CREATE INDEX IX_sourcing_leads_org_created
        ON dbo.sourcing_leads (org_id, created_at DESC);
";

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(sql);
    }
}

