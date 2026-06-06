using Dapper;
using nexthire_api.Data;
using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public class JobBotQuestionRepository : IJobBotQuestionRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public JobBotQuestionRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<JobBotQuestionDto>> ListByJobAsync(Guid orgId, Guid jobId, bool includeInactive)
    {
        const string sql = @"
            SELECT
                id AS Id,
                job_id AS JobId,
                question_key AS QuestionKey,
                question_text AS QuestionText,
                answer_type AS AnswerType,
                sort_order AS SortOrder,
                CAST(is_required AS bit) AS IsRequired,
                CAST(is_active AS bit) AS IsActive,
                created_at AS CreatedAt,
                updated_at AS UpdatedAt
            FROM job_bot_questions
            WHERE org_id = @orgId
              AND job_id = @jobId
              AND (@includeInactive = 1 OR is_active = 1)
            ORDER BY sort_order ASC, created_at ASC, id ASC;";

        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<JobBotQuestionDto>(sql, new { orgId, jobId, includeInactive = includeInactive ? 1 : 0 });
        return rows.ToList();
    }

    public async Task<JobBotQuestionDto?> GetAsync(Guid orgId, Guid jobId, Guid questionId)
    {
        const string sql = @"
            SELECT
                id AS Id,
                job_id AS JobId,
                question_key AS QuestionKey,
                question_text AS QuestionText,
                answer_type AS AnswerType,
                sort_order AS SortOrder,
                CAST(is_required AS bit) AS IsRequired,
                CAST(is_active AS bit) AS IsActive,
                created_at AS CreatedAt,
                updated_at AS UpdatedAt
            FROM job_bot_questions
            WHERE org_id = @orgId
              AND job_id = @jobId
              AND id = @questionId;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<JobBotQuestionDto>(sql, new { orgId, jobId, questionId });
    }

    public async Task<bool> QuestionKeyExistsAsync(Guid jobId, string questionKey, Guid? excludeQuestionId = null)
    {
        const string sql = @"
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM job_bot_questions
                WHERE job_id = @jobId
                  AND question_key = @questionKey
                  AND (@excludeQuestionId IS NULL OR id <> @excludeQuestionId)
            ) THEN 1 ELSE 0 END;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<int>(sql, new { jobId, questionKey, excludeQuestionId }) == 1;
    }

    public async Task<int> GetNextSortOrderAsync(Guid orgId, Guid jobId)
    {
        const string sql = @"
            SELECT COALESCE(MAX(sort_order), -1) + 1
            FROM job_bot_questions
            WHERE org_id = @orgId AND job_id = @jobId;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<int>(sql, new { orgId, jobId });
    }

    public async Task<JobBotQuestionDto> CreateAsync(
        Guid orgId,
        Guid jobId,
        string questionKey,
        CreateJobBotQuestionRequestDto dto,
        string answerType,
        int sortOrder,
        bool isRequired,
        bool isActive)
    {
        const string sql = @"
            INSERT INTO job_bot_questions (
                id, org_id, job_id, question_key, question_text, answer_type,
                sort_order, is_required, is_active, created_at, updated_at
            )
            OUTPUT
                INSERTED.id AS Id,
                INSERTED.job_id AS JobId,
                INSERTED.question_key AS QuestionKey,
                INSERTED.question_text AS QuestionText,
                INSERTED.answer_type AS AnswerType,
                INSERTED.sort_order AS SortOrder,
                CAST(INSERTED.is_required AS bit) AS IsRequired,
                CAST(INSERTED.is_active AS bit) AS IsActive,
                INSERTED.created_at AS CreatedAt,
                INSERTED.updated_at AS UpdatedAt
            VALUES (
                @id, @orgId, @jobId, @questionKey, @questionText, @answerType,
                @sortOrder, @isRequired, @isActive, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()
            );";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QuerySingleAsync<JobBotQuestionDto>(sql, new
        {
            id = Guid.NewGuid(),
            orgId,
            jobId,
            questionKey,
            questionText = dto.QuestionText.Trim(),
            answerType,
            sortOrder,
            isRequired,
            isActive
        });
    }

    public async Task<JobBotQuestionDto?> UpdateAsync(
        Guid orgId,
        Guid jobId,
        Guid questionId,
        UpdateJobBotQuestionRequestDto dto,
        string answerType,
        int sortOrder,
        bool isRequired,
        bool isActive)
    {
        const string sql = @"
            UPDATE job_bot_questions
            SET
                question_text = @questionText,
                answer_type = @answerType,
                sort_order = @sortOrder,
                is_required = @isRequired,
                is_active = @isActive,
                updated_at = SYSDATETIMEOFFSET()
            WHERE org_id = @orgId
              AND job_id = @jobId
              AND id = @questionId;

            SELECT
                id AS Id,
                job_id AS JobId,
                question_key AS QuestionKey,
                question_text AS QuestionText,
                answer_type AS AnswerType,
                sort_order AS SortOrder,
                CAST(is_required AS bit) AS IsRequired,
                CAST(is_active AS bit) AS IsActive,
                created_at AS CreatedAt,
                updated_at AS UpdatedAt
            FROM job_bot_questions
            WHERE org_id = @orgId
              AND job_id = @jobId
              AND id = @questionId;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<JobBotQuestionDto>(sql, new
        {
            orgId,
            jobId,
            questionId,
            questionText = dto.QuestionText.Trim(),
            answerType,
            sortOrder,
            isRequired,
            isActive
        });
    }

    public async Task<bool> DeleteAsync(Guid orgId, Guid jobId, Guid questionId)
    {
        const string sql = @"
            DELETE FROM job_bot_questions
            WHERE org_id = @orgId
              AND job_id = @jobId
              AND id = @questionId;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.ExecuteAsync(sql, new { orgId, jobId, questionId }) == 1;
    }
}
