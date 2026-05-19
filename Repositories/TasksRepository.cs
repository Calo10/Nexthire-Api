using Dapper;
using nexthire_api.Data;
using nexthire_api.Models.Tasks;

namespace nexthire_api.Repositories;

public class TasksRepository : ITasksRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<TasksRepository> _logger;

    public TasksRepository(IDbConnectionFactory connectionFactory, ILogger<TasksRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TaskDto>> GetListAsync(
        Guid orgId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? status,
        Guid? jobId,
        Guid? candidateId,
        Guid? applicationId,
        string? search,
        int page,
        int pageSize,
        string sort,
        string dir)
    {
        var offset = (page - 1) * pageSize;
        var trimmedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();

        var sortNormalized = string.IsNullOrWhiteSpace(sort) ? "created_at" : sort.Trim().ToLowerInvariant();
        var dirNormalized = string.IsNullOrWhiteSpace(dir) ? "desc" : dir.Trim().ToLowerInvariant();

        var orderBy = sortNormalized switch
        {
            "created_at" => "t.created_at",
            "due_at" => "t.due_at",
            _ => throw new ArgumentException("Invalid sort. Allowed: created_at, due_at", nameof(sort))
        };

        var orderDir = dirNormalized switch
        {
            "asc" => "ASC",
            "desc" => "DESC",
            _ => throw new ArgumentException("Invalid dir. Allowed: asc, desc", nameof(dir))
        };

        var sql = $@"
            SELECT
                t.id AS Id,
                t.title AS Title,
                CASE LOWER(t.status)
                    WHEN 'todo' THEN 'todo'
                    WHEN 'to do' THEN 'todo'
                    WHEN 'to-do' THEN 'todo'
                    WHEN 'in_progress' THEN 'in_progress'
                    WHEN 'in progress' THEN 'in_progress'
                    WHEN 'in-progress' THEN 'in_progress'
                    WHEN 'blocked' THEN 'blocked'
                    WHEN 'done' THEN 'done'
                    ELSE LOWER(t.status)
                END AS Status,
                t.due_at AS DueAt,
                t.application_id AS ApplicationId,
                a.job_id AS JobId,
                a.candidate_id AS CandidateId,
                t.assigned_to_user_id AS AssignedToUserId,
                j.title AS JobTitle,
                (c.first_name + ' ' + c.last_name) AS CandidateName,
                COALESCE(u.display_name, u.email) AS AssignedToName,
                t.created_at AS CreatedAt,
                t.completed_at AS CompletedAt
            FROM tasks t
            INNER JOIN applications a ON a.id = t.application_id
            LEFT JOIN jobs j ON j.id = a.job_id AND j.org_id = @orgId
            LEFT JOIN candidates c ON c.id = a.candidate_id AND c.org_id = @orgId
            LEFT JOIN nh_users u ON u.id = t.assigned_to_user_id AND u.org_id = @orgId
            WHERE a.org_id = @orgId
              AND (@status IS NULL OR LOWER(t.status) = LOWER(@status))
              AND (@jobId IS NULL OR a.job_id = @jobId)
              AND (@candidateId IS NULL OR a.candidate_id = @candidateId)
              AND (@applicationId IS NULL OR t.application_id = @applicationId)
              AND (@search IS NULL OR LOWER(t.title) LIKE '%' + LOWER(@search) + '%')
              -- from/to filter by due_at (tasks without due_at are excluded when from/to is provided)
              AND (@from IS NULL OR (t.due_at IS NOT NULL AND t.due_at >= @from))
              AND (@to IS NULL OR (t.due_at IS NOT NULL AND t.due_at <= @to))
            ORDER BY {orderBy} {orderDir}, t.id {orderDir}
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;";

        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<TaskDto>(sql, new
        {
            orgId,
            from,
            to,
            status,
            jobId,
            candidateId,
            applicationId,
            search = trimmedSearch,
            offset,
            pageSize
        });

        return rows.ToList();
    }

    public async Task<IReadOnlyList<TaskDto>> GetAllAsync(Guid orgId)
    {
        const string sql = @"
            SELECT
                t.id AS Id,
                t.title AS Title,
                CASE LOWER(t.status)
                    WHEN 'todo' THEN 'todo'
                    WHEN 'to do' THEN 'todo'
                    WHEN 'to-do' THEN 'todo'
                    WHEN 'in_progress' THEN 'in_progress'
                    WHEN 'in progress' THEN 'in_progress'
                    WHEN 'in-progress' THEN 'in_progress'
                    WHEN 'blocked' THEN 'blocked'
                    WHEN 'done' THEN 'done'
                    ELSE LOWER(t.status)
                END AS Status,
                t.due_at AS DueAt,
                t.application_id AS ApplicationId,
                a.job_id AS JobId,
                a.candidate_id AS CandidateId,
                t.assigned_to_user_id AS AssignedToUserId,
                j.title AS JobTitle,
                (c.first_name + ' ' + c.last_name) AS CandidateName,
                COALESCE(u.display_name, u.email) AS AssignedToName,
                t.created_at AS CreatedAt,
                t.completed_at AS CompletedAt
            FROM tasks t
            INNER JOIN applications a ON a.id = t.application_id
            LEFT JOIN jobs j ON j.id = a.job_id AND j.org_id = @orgId
            LEFT JOIN candidates c ON c.id = a.candidate_id AND c.org_id = @orgId
            LEFT JOIN nh_users u ON u.id = t.assigned_to_user_id AND u.org_id = @orgId
            WHERE a.org_id = @orgId
            ORDER BY t.created_at DESC;";

        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<TaskDto>(sql, new { orgId });
        return rows.ToList();
    }

    public async Task<TaskDto?> GetByIdAsync(Guid orgId, Guid id)
    {
        const string sql = @"
            SELECT
                t.id AS Id,
                t.title AS Title,
                CASE LOWER(t.status)
                    WHEN 'todo' THEN 'todo'
                    WHEN 'to do' THEN 'todo'
                    WHEN 'to-do' THEN 'todo'
                    WHEN 'in_progress' THEN 'in_progress'
                    WHEN 'in progress' THEN 'in_progress'
                    WHEN 'in-progress' THEN 'in_progress'
                    WHEN 'blocked' THEN 'blocked'
                    WHEN 'done' THEN 'done'
                    ELSE LOWER(t.status)
                END AS Status,
                t.due_at AS DueAt,
                t.application_id AS ApplicationId,
                a.job_id AS JobId,
                a.candidate_id AS CandidateId,
                t.assigned_to_user_id AS AssignedToUserId,
                j.title AS JobTitle,
                (c.first_name + ' ' + c.last_name) AS CandidateName,
                COALESCE(u.display_name, u.email) AS AssignedToName,
                t.created_at AS CreatedAt,
                t.completed_at AS CompletedAt
            FROM tasks t
            INNER JOIN applications a ON a.id = t.application_id
            LEFT JOIN jobs j ON j.id = a.job_id AND j.org_id = @orgId
            LEFT JOIN candidates c ON c.id = a.candidate_id AND c.org_id = @orgId
            LEFT JOIN nh_users u ON u.id = t.assigned_to_user_id AND u.org_id = @orgId
            WHERE a.org_id = @orgId AND t.id = @id;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<TaskDto>(sql, new { orgId, id });
    }

    public async Task<IReadOnlyList<TaskDto>> GetByApplicationIdAsync(Guid orgId, Guid applicationId)
    {
        const string sql = @"
            SELECT
                t.id AS Id,
                t.title AS Title,
                CASE LOWER(t.status)
                    WHEN 'todo' THEN 'todo'
                    WHEN 'to do' THEN 'todo'
                    WHEN 'to-do' THEN 'todo'
                    WHEN 'in_progress' THEN 'in_progress'
                    WHEN 'in progress' THEN 'in_progress'
                    WHEN 'in-progress' THEN 'in_progress'
                    WHEN 'blocked' THEN 'blocked'
                    WHEN 'done' THEN 'done'
                    ELSE LOWER(t.status)
                END AS Status,
                t.due_at AS DueAt,
                t.application_id AS ApplicationId,
                a.job_id AS JobId,
                a.candidate_id AS CandidateId,
                t.assigned_to_user_id AS AssignedToUserId,
                j.title AS JobTitle,
                (c.first_name + ' ' + c.last_name) AS CandidateName,
                COALESCE(u.display_name, u.email) AS AssignedToName,
                t.created_at AS CreatedAt,
                t.completed_at AS CompletedAt
            FROM tasks t
            INNER JOIN applications a ON a.id = t.application_id
            LEFT JOIN jobs j ON j.id = a.job_id AND j.org_id = @orgId
            LEFT JOIN candidates c ON c.id = a.candidate_id AND c.org_id = @orgId
            LEFT JOIN nh_users u ON u.id = t.assigned_to_user_id AND u.org_id = @orgId
            WHERE a.org_id = @orgId AND t.application_id = @applicationId
            ORDER BY t.created_at DESC;";

        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<TaskDto>(sql, new { orgId, applicationId });
        return rows.ToList();
    }

    public async Task<Guid?> GetTaskOrgIdAsync(Guid taskId)
    {
        const string sql = @"
            SELECT a.org_id
            FROM tasks t
            INNER JOIN applications a ON a.id = t.application_id
            WHERE t.id = @taskId;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<Guid?>(sql, new { taskId });
    }

    public async Task<Guid?> GetApplicationOrgIdAsync(Guid applicationId)
    {
        const string sql = @"SELECT org_id FROM applications WHERE id = @applicationId;";
        using var connection = _connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<Guid?>(sql, new { applicationId });
    }

    public async Task<Guid?> GetUserOrgIdAsync(Guid userId)
    {
        const string sql = @"SELECT org_id FROM nh_users WHERE id = @userId;";
        using var connection = _connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<Guid?>(sql, new { userId });
    }

    public async Task<TaskDto?> CreateAsync(Guid orgId, Guid id, CreateTaskRequest request, string statusNormalized)
    {
        const string sql = @"
            INSERT INTO tasks (id, application_id, assigned_to_user_id, title, due_at, status, created_at, completed_at)
            VALUES (
                @Id,
                @ApplicationId,
                @AssignedToUserId,
                @Title,
                @DueAt,
                @Status,
                TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'),
                CASE WHEN LOWER(@Status) = 'done' THEN TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') ELSE NULL END
            );

            SELECT
                t.id AS Id,
                t.title AS Title,
                CASE LOWER(t.status)
                    WHEN 'todo' THEN 'todo'
                    WHEN 'to do' THEN 'todo'
                    WHEN 'to-do' THEN 'todo'
                    WHEN 'in_progress' THEN 'in_progress'
                    WHEN 'in progress' THEN 'in_progress'
                    WHEN 'in-progress' THEN 'in_progress'
                    WHEN 'blocked' THEN 'blocked'
                    WHEN 'done' THEN 'done'
                    ELSE LOWER(t.status)
                END AS Status,
                t.due_at AS DueAt,
                t.application_id AS ApplicationId,
                a.job_id AS JobId,
                a.candidate_id AS CandidateId,
                t.assigned_to_user_id AS AssignedToUserId,
                j.title AS JobTitle,
                (c.first_name + ' ' + c.last_name) AS CandidateName,
                COALESCE(u.display_name, u.email) AS AssignedToName,
                t.created_at AS CreatedAt,
                t.completed_at AS CompletedAt
            FROM tasks t
            INNER JOIN applications a ON a.id = t.application_id
            LEFT JOIN jobs j ON j.id = a.job_id AND j.org_id = @OrgId
            LEFT JOIN candidates c ON c.id = a.candidate_id AND c.org_id = @OrgId
            LEFT JOIN nh_users u ON u.id = t.assigned_to_user_id AND u.org_id = @OrgId
            WHERE a.org_id = @OrgId AND t.id = @Id;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<TaskDto>(sql, new
        {
            OrgId = orgId,
            Id = id,
            ApplicationId = request.ApplicationId,
            AssignedToUserId = request.AssignedToUserId,
            Title = request.Title.Trim(),
            DueAt = request.DueAt,
            Status = statusNormalized
        });
    }

    public async Task<bool> UpdateStatusAsync(Guid orgId, Guid id, string statusNormalized)
    {
        const string sql = @"
            UPDATE t
            SET
                t.status = @status,
                t.completed_at = CASE WHEN LOWER(@status) = 'done'
                                      THEN TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
                                      ELSE NULL
                                 END
            FROM tasks t
            INNER JOIN applications a ON a.id = t.application_id
            WHERE a.org_id = @orgId AND t.id = @id;";

        using var connection = _connectionFactory.CreateConnection();
        var affected = await connection.ExecuteAsync(sql, new { orgId, id, status = statusNormalized });
        return affected > 0;
    }

    public async Task<bool> DeleteAsync(Guid orgId, Guid id)
    {
        const string sql = @"
            DELETE t
            FROM tasks t
            INNER JOIN applications a ON a.id = t.application_id
            WHERE a.org_id = @orgId AND t.id = @id;";

        using var connection = _connectionFactory.CreateConnection();
        var affected = await connection.ExecuteAsync(sql, new { orgId, id });
        return affected > 0;
    }
}

