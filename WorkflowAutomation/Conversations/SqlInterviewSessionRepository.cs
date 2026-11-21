using System.Text.Json;
using Dapper;
using nexthire_api.WorkflowAutomation.Repositories;

namespace nexthire_api.WorkflowAutomation.Conversations;

public interface IInterviewSessionRepository
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken = default);
    Task UpsertAsync(InterviewSession session, CancellationToken cancellationToken = default);
    Task<bool> TryAdvanceAsync(
        InterviewSession session,
        int expectedQuestionIndex,
        CancellationToken cancellationToken = default);
    Task<InterviewSession?> GetByConversationIdAsync(
        string conversationId,
        CancellationToken cancellationToken = default);
    Task<InterviewSession?> GetActiveByConversationIdAsync(
        string conversationId,
        CancellationToken cancellationToken = default);
    Task<InterviewSession?> GetActiveByOrganizationAndRecipientAsync(
        Guid organizationId,
        string recipient,
        CancellationToken cancellationToken = default);
}

public sealed class SqlInterviewSessionRepository : IInterviewSessionRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IWorkflowSqlSession _sql;

    public SqlInterviewSessionRepository(IWorkflowSqlSession sql)
    {
        _sql = sql;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        // Ensured with the rest of workflow schema via SqlWorkflowDefinitionRepository.
        await Task.CompletedTask;
    }

    public async Task UpsertAsync(InterviewSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        const string sql = @"
MERGE dbo.nh_wf_interview_sessions AS target
USING (SELECT @ConversationId AS conversation_id) AS source
ON target.conversation_id = source.conversation_id
WHEN MATCHED THEN
    UPDATE SET
        organization_id         = @OrganizationId,
        workflow_execution_id   = @WorkflowExecutionId,
        channel                 = @Channel,
        recipient               = @Recipient,
        question_index          = @QuestionIndex,
        answers                 = @Answers,
        questions               = @Questions,
        completion_event_type   = @CompletionEventType,
        status                  = @Status,
        updated_at              = @UpdatedAt
WHEN NOT MATCHED THEN
    INSERT (
        conversation_id,
        organization_id,
        workflow_execution_id,
        channel,
        recipient,
        question_index,
        answers,
        questions,
        completion_event_type,
        status,
        created_at,
        updated_at
    )
    VALUES (
        @ConversationId,
        @OrganizationId,
        @WorkflowExecutionId,
        @Channel,
        @Recipient,
        @QuestionIndex,
        @Answers,
        @Questions,
        @CompletionEventType,
        @Status,
        @CreatedAt,
        @UpdatedAt
    );";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        await connection.ExecuteAsync(
            scope.CreateCommand(sql, ToParameters(session), cancellationToken));
    }

    public async Task<bool> TryAdvanceAsync(
        InterviewSession session,
        int expectedQuestionIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        const string sql = @"
UPDATE dbo.nh_wf_interview_sessions
SET
    question_index        = @QuestionIndex,
    answers               = @Answers,
    status                = @Status,
    updated_at            = @UpdatedAt
WHERE conversation_id = @ConversationId
  AND status = 'Active'
  AND question_index = @ExpectedQuestionIndex;";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        var rows = await connection.ExecuteAsync(
            scope.CreateCommand(
                sql,
                new
                {
                    session.ConversationId,
                    session.QuestionIndex,
                    Answers = JsonSerializer.Serialize(session.Answers, JsonOptions),
                    session.Status,
                    session.UpdatedAt,
                    ExpectedQuestionIndex = expectedQuestionIndex
                },
                cancellationToken));

        return rows > 0;
    }

    public async Task<InterviewSession?> GetByConversationIdAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        const string sql = @"
SELECT
    conversation_id         AS ConversationId,
    organization_id         AS OrganizationId,
    workflow_execution_id   AS WorkflowExecutionId,
    channel                 AS Channel,
    recipient               AS Recipient,
    question_index          AS QuestionIndex,
    answers                 AS AnswersJson,
    questions               AS QuestionsJson,
    completion_event_type   AS CompletionEventType,
    status                  AS Status,
    created_at              AS CreatedAt,
    updated_at              AS UpdatedAt
FROM dbo.nh_wf_interview_sessions
WHERE conversation_id = @ConversationId;";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        var row = await connection.QuerySingleOrDefaultAsync<InterviewSessionRow>(
            scope.CreateCommand(sql, new { ConversationId = conversationId }, cancellationToken));

        return row is null ? null : FromRow(row);
    }
    public async Task<InterviewSession?> GetActiveByConversationIdAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        const string sql = @"
SELECT
    conversation_id         AS ConversationId,
    organization_id         AS OrganizationId,
    workflow_execution_id   AS WorkflowExecutionId,
    channel                 AS Channel,
    recipient               AS Recipient,
    question_index          AS QuestionIndex,
    answers                 AS AnswersJson,
    questions               AS QuestionsJson,
    completion_event_type   AS CompletionEventType,
    status                  AS Status,
    created_at              AS CreatedAt,
    updated_at              AS UpdatedAt
FROM dbo.nh_wf_interview_sessions
WHERE conversation_id = @ConversationId
  AND status = 'Active';";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        var row = await connection.QuerySingleOrDefaultAsync<InterviewSessionRow>(
            scope.CreateCommand(sql, new { ConversationId = conversationId }, cancellationToken));

        return row is null ? null : FromRow(row);
    }

    public async Task<InterviewSession?> GetActiveByOrganizationAndRecipientAsync(
        Guid organizationId,
        string recipient,
        CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty)
            throw new ArgumentException("OrganizationId is required.", nameof(organizationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);

        const string sql = @"
SELECT TOP (1)
    conversation_id         AS ConversationId,
    organization_id         AS OrganizationId,
    workflow_execution_id   AS WorkflowExecutionId,
    channel                 AS Channel,
    recipient               AS Recipient,
    question_index          AS QuestionIndex,
    answers                 AS AnswersJson,
    questions               AS QuestionsJson,
    completion_event_type   AS CompletionEventType,
    status                  AS Status,
    created_at              AS CreatedAt,
    updated_at              AS UpdatedAt
FROM dbo.nh_wf_interview_sessions
WHERE organization_id = @OrganizationId
  AND recipient = @Recipient
  AND status = 'Active'
ORDER BY updated_at DESC;";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        var row = await connection.QuerySingleOrDefaultAsync<InterviewSessionRow>(
            scope.CreateCommand(
                sql,
                new { OrganizationId = organizationId, Recipient = recipient },
                cancellationToken));

        return row is null ? null : FromRow(row);
    }

    private static object ToParameters(InterviewSession session) =>
        new
        {
            session.ConversationId,
            session.OrganizationId,
            session.WorkflowExecutionId,
            session.Channel,
            session.Recipient,
            session.QuestionIndex,
            Answers = JsonSerializer.Serialize(session.Answers, JsonOptions),
            Questions = JsonSerializer.Serialize(session.Questions, JsonOptions),
            session.CompletionEventType,
            session.Status,
            session.CreatedAt,
            session.UpdatedAt
        };

    private static InterviewSession FromRow(InterviewSessionRow row)
    {
        var answers = string.IsNullOrWhiteSpace(row.AnswersJson)
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : JsonSerializer.Deserialize<Dictionary<string, string>>(row.AnswersJson, JsonOptions)
              ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var questions = string.IsNullOrWhiteSpace(row.QuestionsJson)
            ? (IReadOnlyList<InterviewQuestionDefinition>)Array.Empty<InterviewQuestionDefinition>()
            : (IReadOnlyList<InterviewQuestionDefinition>)(
                JsonSerializer.Deserialize<List<InterviewQuestionDefinition>>(row.QuestionsJson, JsonOptions)
                ?? new List<InterviewQuestionDefinition>());

        return new InterviewSession
        {
            ConversationId = row.ConversationId,
            OrganizationId = row.OrganizationId,
            WorkflowExecutionId = row.WorkflowExecutionId,
            Channel = row.Channel,
            Recipient = row.Recipient,
            QuestionIndex = row.QuestionIndex,
            Answers = new Dictionary<string, string>(answers, StringComparer.OrdinalIgnoreCase),
            Questions = questions,
            CompletionEventType = row.CompletionEventType,
            Status = row.Status,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt
        };
    }

    private sealed class InterviewSessionRow
    {
        public string ConversationId { get; set; } = string.Empty;
        public Guid OrganizationId { get; set; }
        public Guid WorkflowExecutionId { get; set; }
        public string Channel { get; set; } = string.Empty;
        public string Recipient { get; set; } = string.Empty;
        public int QuestionIndex { get; set; }
        public string AnswersJson { get; set; } = "{}";
        public string QuestionsJson { get; set; } = "[]";
        public string CompletionEventType { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
