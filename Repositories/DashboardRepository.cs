using System.Data;
using Dapper;
using nexthire_api.Data;
using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public class DashboardRepository : IDashboardRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<DashboardRepository> _logger;

    public DashboardRepository(IDbConnectionFactory connectionFactory, ILogger<DashboardRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<DashboardSummaryDto> GetSummaryAsync(Guid orgId, DateTimeOffset? from, DateTimeOffset? to, Guid? jobId)
    {
        var sql = @"
            SELECT 
                (SELECT COUNT(*) 
                 FROM jobs j 
                 WHERE j.org_id = @orgId AND LOWER(j.status) IN ('open','active')) AS OpenJobs,
                (SELECT COUNT(*) 
                 FROM candidates c
                 WHERE c.org_id = @orgId
                   AND (@from IS NULL OR c.created_at >= @from) 
                   AND (@to IS NULL OR c.created_at <= @to)) AS NewCandidates,
                (SELECT COUNT(*) 
                 FROM applications a
                 WHERE a.org_id = @orgId
                   AND (@from IS NULL OR a.created_at >= @from) 
                   AND (@to IS NULL OR a.created_at <= @to)
                   AND (@jobId IS NULL OR a.job_id = @jobId)) AS NewApplications,
                (SELECT COUNT(*)
                 FROM tasks t
                 INNER JOIN applications a ON a.id = t.application_id
                 WHERE a.org_id = @orgId
                   AND t.due_at IS NOT NULL
                   AND LOWER(t.status) IN ('pending','in progress')
                   AND LOWER(t.title) LIKE '%interview%'
                   AND (@from IS NULL OR t.due_at >= @from)
                   AND (@to IS NULL OR t.due_at <= @to)
                   AND (@jobId IS NULL OR a.job_id = @jobId)) AS UpcomingInterviews,
                (SELECT COUNT(*) 
                 FROM applications a
                 INNER JOIN pipeline_stages ps ON ps.id = a.current_stage_id
                 WHERE a.org_id = @orgId
                   AND LOWER(ps.name) = 'offer sent'
                   AND (@from IS NULL OR a.updated_at >= @from)
                   AND (@to IS NULL OR a.updated_at <= @to)
                   AND (@jobId IS NULL OR a.job_id = @jobId)) AS OffersSent,
                (SELECT AVG(CAST(DATEDIFF(DAY, COALESCE(a.applied_at, a.created_at), a.updated_at) AS decimal(10,2)))
                 FROM applications a
                 INNER JOIN pipeline_stages ps ON ps.id = a.current_stage_id
                 WHERE a.org_id = @orgId
                   AND LOWER(ps.name) = 'hired'
                   AND (@from IS NULL OR a.created_at >= @from)
                   AND (@to IS NULL OR a.created_at <= @to)
                   AND (@jobId IS NULL OR a.job_id = @jobId)
                   AND DATEDIFF(DAY, COALESCE(a.applied_at, a.created_at), a.updated_at) >= 0) AS AvgTimeToHireDays";

        using var connection = _connectionFactory.CreateConnection();
        var result = await connection.QueryFirstOrDefaultAsync<DashboardSummaryDto>(sql, new { orgId, from, to, jobId });
        return result ?? new DashboardSummaryDto();
    }

    public async Task<IEnumerable<ApplicationsByStageDto>> GetApplicationsByStageAsync(Guid orgId, DateTimeOffset? from, DateTimeOffset? to, Guid? jobId)
    {
        var sql = @"
            SELECT 
                ps.id AS StageId,
                ps.name AS StageName,
                COUNT(a.id) AS Count
            FROM pipeline_stages ps
            LEFT JOIN applications a ON a.current_stage_id = ps.id
                AND a.org_id = @orgId
                AND (@from IS NULL OR a.created_at >= @from)
                AND (@to IS NULL OR a.created_at <= @to)
                AND (@jobId IS NULL OR a.job_id = @jobId)
            WHERE ps.org_id = @orgId
            GROUP BY ps.id, ps.name, ps.sort_order
            ORDER BY ps.sort_order";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryAsync<ApplicationsByStageDto>(sql, new { orgId, from, to, jobId });
    }

    public async Task<IEnumerable<ActivityTrendDto>> GetActivityTrendAsync(Guid orgId, DateTimeOffset? from, DateTimeOffset? to, Guid? jobId)
    {
        var sql = @"
            SELECT 
                CAST(created_at AS DATE) AS Date,
                COUNT(CASE WHEN source = 'application' THEN 1 END) AS Applications,
                COUNT(CASE WHEN source = 'candidate' THEN 1 END) AS Candidates
            FROM (
                SELECT created_at, 'application' AS source, job_id 
                FROM applications
                WHERE org_id = @orgId
                  AND (@from IS NULL OR created_at >= @from)
                  AND (@to IS NULL OR created_at <= @to)
                  AND (@jobId IS NULL OR job_id = @jobId)
                UNION ALL
                SELECT created_at, 'candidate' AS source, NULL AS job_id
                FROM candidates
                WHERE org_id = @orgId
                  AND (@from IS NULL OR created_at >= @from)
                  AND (@to IS NULL OR created_at <= @to)
            ) AS activity
            GROUP BY CAST(created_at AS DATE)
            ORDER BY Date";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryAsync<ActivityTrendDto>(sql, new { orgId, from, to, jobId });
    }

    public async Task<IEnumerable<MyJobDto>> GetMyJobsAsync(Guid orgId, DateTimeOffset? from, DateTimeOffset? to)
    {
        var sql = @"
            SELECT TOP 20
                j.id AS JobId,
                j.title AS Title,
                j.status AS Status,
                j.created_at AS CreatedAt,
                COUNT(DISTINCT a.id) AS ApplicationsCount,
                MAX(a.updated_at) AS LastActivityAt
            FROM jobs j
            LEFT JOIN applications a ON a.job_id = j.id AND a.org_id = @orgId
            WHERE j.org_id = @orgId
              AND (@from IS NULL OR j.created_at >= @from)
              AND (@to IS NULL OR j.created_at <= @to)
            GROUP BY j.id, j.title, j.status, j.created_at
            ORDER BY LastActivityAt DESC, j.created_at DESC";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryAsync<MyJobDto>(sql, new { orgId, from, to });
    }

    public async Task<IEnumerable<RecentCandidateDto>> GetRecentCandidatesAsync(Guid orgId, int limit)
    {
        var sql = @"
            SELECT TOP (@limit)
                c.id AS CandidateId,
                c.first_name + ' ' + c.last_name AS FullName,
                c.email AS Email,
                ps.name AS CurrentStageName,
                c.updated_at AS UpdatedAt
            FROM candidates c
            OUTER APPLY (
                SELECT TOP 1 a2.current_stage_id
                FROM applications a2
                WHERE a2.org_id = @orgId AND a2.candidate_id = c.id
                ORDER BY a2.updated_at DESC
            ) lastApp
            LEFT JOIN pipeline_stages ps ON lastApp.current_stage_id = ps.id
            WHERE c.org_id = @orgId
            ORDER BY c.updated_at DESC";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryAsync<RecentCandidateDto>(sql, new { orgId, limit });
    }

    public async Task<IEnumerable<UpcomingTaskDto>> GetUpcomingTasksAsync(Guid orgId, int limit)
    {
        var sql = @"
            SELECT TOP (@limit)
                t.id AS TaskId,
                t.title AS Title,
                t.due_at AS DueAt,
                t.status AS Status,
                a.job_id AS RelatedJobId,
                a.candidate_id AS RelatedCandidateId
            FROM tasks t
            INNER JOIN applications a ON a.id = t.application_id
            WHERE a.org_id = @orgId
              AND t.due_at IS NOT NULL
              AND t.completed_at IS NULL
            ORDER BY t.due_at ASC";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryAsync<UpcomingTaskDto>(sql, new { orgId, limit });
    }
}
