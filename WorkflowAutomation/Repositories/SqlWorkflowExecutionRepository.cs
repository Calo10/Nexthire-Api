using Dapper;
using nexthire_api.WorkflowAutomation.DTOs;
using nexthire_api.WorkflowAutomation.Mapping;
using nexthire_api.WorkflowAutomation.Models;
using Nexa.WorkflowAutomation.Contracts.Persistence;
using Nexa.WorkflowAutomation.Exceptions;
using Nexa.WorkflowAutomation.Models.Executions;

namespace nexthire_api.WorkflowAutomation.Repositories;

public sealed class SqlWorkflowExecutionRepository : IWorkflowExecutionRepository, IWorkflowDueExecutionLookup
{
    private readonly IWorkflowSqlSession _sql;

    public SqlWorkflowExecutionRepository(IWorkflowSqlSession sql)
    {
        _sql = sql;
    }

    public async Task<IReadOnlyList<WorkflowExecutionListItemResponse>> ListAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT
    id                      AS Id,
    workflow_definition_id  AS WorkflowDefinitionId,
    entity_type             AS EntityType,
    entity_id               AS EntityId,
    status                  AS Status,
    current_step_id         AS CurrentStepId,
    created_at              AS CreatedAt,
    updated_at              AS UpdatedAt,
    revision                AS Revision
FROM dbo.nh_wf_executions
WHERE organization_id = @OrganizationId
ORDER BY created_at DESC;";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        var rows = await connection.QueryAsync<WorkflowExecutionRecord>(
            scope.CreateCommand(sql, new { OrganizationId = organizationId }, cancellationToken));

        return rows.Select(WorkflowExecutionMapper.ToListItem).ToList();
    }

    public async Task<WorkflowExecutionRecord?> GetRecordByIdAsync(
        Guid organizationId,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT
    id                          AS Id,
    organization_id             AS OrganizationId,
    workflow_definition_id      AS WorkflowDefinitionId,
    workflow_version            AS WorkflowVersion,
    entity_type                 AS EntityType,
    entity_id                   AS EntityId,
    status                      AS Status,
    current_step_id             AS CurrentStepId,
    current_step_execution_id   AS CurrentStepExecutionId,
    conversation_id             AS ConversationId,
    context                     AS Context,
    created_at                  AS CreatedAt,
    updated_at                  AS UpdatedAt,
    started_at                  AS StartedAt,
    waiting_since               AS WaitingSince,
    resume_at                   AS ResumeAt,
    completed_at                AS CompletedAt,
    cancelled_at                AS CancelledAt,
    failed_at                   AS FailedAt,
    failure_reason              AS FailureReason,
    revision                    AS Revision
FROM dbo.nh_wf_executions
WHERE organization_id = @OrganizationId
  AND id = @Id;";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        return await connection.QuerySingleOrDefaultAsync<WorkflowExecutionRecord>(
            scope.CreateCommand(
                sql,
                new { OrganizationId = organizationId, Id = id },
                cancellationToken));
    }

    public async Task<WorkflowExecution?> GetByIdAsync(
        Guid organizationId,
        Guid id,
        CancellationToken cancellationToken)
    {
        var row = await GetRecordByIdAsync(organizationId, id, cancellationToken);
        return row is null ? null : WorkflowExecutionMapper.FromRecord(row);
    }

    public async Task<bool> ExistsAsync(
        Guid organizationId,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT CASE WHEN EXISTS (
    SELECT 1
    FROM dbo.nh_wf_executions
    WHERE organization_id = @OrganizationId
      AND id = @Id
) THEN 1 ELSE 0 END;";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        var exists = await connection.ExecuteScalarAsync<int>(
            scope.CreateCommand(
                sql,
                new { OrganizationId = organizationId, Id = id },
                cancellationToken));

        return exists == 1;
    }

    public async Task AddAsync(WorkflowExecution execution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);

        const string sql = @"
INSERT INTO dbo.nh_wf_executions (
    id,
    organization_id,
    workflow_definition_id,
    workflow_version,
    entity_type,
    entity_id,
    status,
    current_step_id,
    current_step_execution_id,
    conversation_id,
    context,
    created_at,
    updated_at,
    started_at,
    waiting_since,
    resume_at,
    completed_at,
    cancelled_at,
    failed_at,
    failure_reason,
    revision
)
VALUES (
    @Id,
    @OrganizationId,
    @WorkflowDefinitionId,
    @WorkflowVersion,
    @EntityType,
    @EntityId,
    @Status,
    @CurrentStepId,
    @CurrentStepExecutionId,
    @ConversationId,
    @Context,
    @CreatedAt,
    @UpdatedAt,
    @StartedAt,
    @WaitingSince,
    @ResumeAt,
    @CompletedAt,
    @CancelledAt,
    @FailedAt,
    @FailureReason,
    @Revision
);";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        await connection.ExecuteAsync(
            scope.CreateCommand(
                sql,
                ToWriteParameters(execution, updatedAt: null),
                cancellationToken));
    }

    public async Task UpdateAsync(
        WorkflowExecution execution,
        int expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);

        var newRevision = expectedRevision + 1;
        execution.Revision = newRevision;

        const string sql = @"
UPDATE dbo.nh_wf_executions
SET
    workflow_definition_id      = @WorkflowDefinitionId,
    workflow_version            = @WorkflowVersion,
    entity_type                 = @EntityType,
    entity_id                   = @EntityId,
    status                      = @Status,
    current_step_id             = @CurrentStepId,
    current_step_execution_id   = @CurrentStepExecutionId,
    conversation_id             = @ConversationId,
    context                     = @Context,
    updated_at                  = @UpdatedAt,
    started_at                  = @StartedAt,
    waiting_since               = @WaitingSince,
    resume_at                   = @ResumeAt,
    completed_at                = @CompletedAt,
    cancelled_at                = @CancelledAt,
    failed_at                   = @FailedAt,
    failure_reason              = @FailureReason,
    revision                    = @Revision
WHERE organization_id = @OrganizationId
  AND id = @Id
  AND revision = @ExpectedRevision;";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        var rows = await connection.ExecuteAsync(
            scope.CreateCommand(
                sql,
                new
                {
                    execution.Id,
                    execution.OrganizationId,
                    execution.WorkflowDefinitionId,
                    execution.WorkflowVersion,
                    execution.EntityType,
                    execution.EntityId,
                    Status = execution.Status.ToString(),
                    execution.CurrentStepId,
                    execution.CurrentStepExecutionId,
                    execution.ConversationId,
                    Context = WorkflowExecutionMapper.SerializeContext(execution.Context),
                    UpdatedAt = DateTimeOffset.UtcNow,
                    execution.StartedAt,
                    execution.WaitingSince,
                    execution.ResumeAt,
                    execution.CompletedAt,
                    execution.CancelledAt,
                    execution.FailedAt,
                    execution.FailureReason,
                    Revision = newRevision,
                    ExpectedRevision = expectedRevision
                },
                cancellationToken));

        if (rows == 0)
        {
            execution.Revision = expectedRevision;
            throw new WorkflowConcurrencyException(execution.Id, expectedRevision);
        }
    }

    public async Task<IReadOnlyList<WorkflowExecution>> FindWaitingExecutionsAsync(
        WaitingExecutionQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var limit = query.Limit <= 0 ? 100 : Math.Min(query.Limit, 500);

        const string sql = @"
SELECT TOP (@Limit)
    e.id                          AS Id,
    e.organization_id             AS OrganizationId,
    e.workflow_definition_id      AS WorkflowDefinitionId,
    e.workflow_version            AS WorkflowVersion,
    e.entity_type                 AS EntityType,
    e.entity_id                   AS EntityId,
    e.status                      AS Status,
    e.current_step_id             AS CurrentStepId,
    e.current_step_execution_id   AS CurrentStepExecutionId,
    e.conversation_id             AS ConversationId,
    e.context                     AS Context,
    e.created_at                  AS CreatedAt,
    e.updated_at                  AS UpdatedAt,
    e.started_at                  AS StartedAt,
    e.waiting_since               AS WaitingSince,
    e.resume_at                   AS ResumeAt,
    e.completed_at                AS CompletedAt,
    e.cancelled_at                AS CancelledAt,
    e.failed_at                   AS FailedAt,
    e.failure_reason              AS FailureReason,
    e.revision                    AS Revision
FROM dbo.nh_wf_executions e
INNER JOIN dbo.nh_wf_step_executions se
    ON se.id = e.current_step_execution_id
   AND se.workflow_execution_id = e.id
WHERE e.organization_id = @OrganizationId
  AND e.status = 'Waiting'
  AND se.status = 'Waiting'
  AND (@EventType IS NULL OR se.waiting_event_type = @EventType)
  AND (@CorrelationId IS NULL OR se.correlation_id = @CorrelationId)
  AND (@EntityType IS NULL OR e.entity_type = @EntityType)
  AND (@EntityId IS NULL OR e.entity_id = @EntityId)
ORDER BY e.waiting_since ASC;";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        var rows = await connection.QueryAsync<WorkflowExecutionRecord>(
            scope.CreateCommand(
                sql,
                new
                {
                    query.OrganizationId,
                    query.EventType,
                    query.CorrelationId,
                    query.EntityType,
                    query.EntityId,
                    Limit = limit
                },
                cancellationToken));

        return rows.Select(WorkflowExecutionMapper.FromRecord).ToList();
    }

    public async Task<IReadOnlyList<WorkflowExecution>> GetDueExecutionsAsync(
        DueExecutionQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var limit = query.Limit <= 0 ? 100 : Math.Min(query.Limit, 500);

        const string sql = @"
SELECT TOP (@Limit)
    id                          AS Id,
    organization_id             AS OrganizationId,
    workflow_definition_id      AS WorkflowDefinitionId,
    workflow_version            AS WorkflowVersion,
    entity_type                 AS EntityType,
    entity_id                   AS EntityId,
    status                      AS Status,
    current_step_id             AS CurrentStepId,
    current_step_execution_id   AS CurrentStepExecutionId,
    conversation_id             AS ConversationId,
    context                     AS Context,
    created_at                  AS CreatedAt,
    updated_at                  AS UpdatedAt,
    started_at                  AS StartedAt,
    waiting_since               AS WaitingSince,
    resume_at                   AS ResumeAt,
    completed_at                AS CompletedAt,
    cancelled_at                AS CancelledAt,
    failed_at                   AS FailedAt,
    failure_reason              AS FailureReason,
    revision                    AS Revision
FROM dbo.nh_wf_executions
WHERE organization_id = @OrganizationId
  AND status = 'Waiting'
  AND resume_at IS NOT NULL
  AND resume_at <= @AsOf
ORDER BY resume_at ASC;";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        var rows = await connection.QueryAsync<WorkflowExecutionRecord>(
            scope.CreateCommand(
                sql,
                new
                {
                    query.OrganizationId,
                    query.AsOf,
                    Limit = limit
                },
                cancellationToken));

        return rows.Select(WorkflowExecutionMapper.FromRecord).ToList();
    }

    public async Task<IReadOnlyList<WorkflowExecution>> ListDueAsync(
        DateTimeOffset asOf,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var take = limit <= 0 ? 100 : Math.Min(limit, 500);

        const string sql = @"
SELECT TOP (@Limit)
    id                          AS Id,
    organization_id             AS OrganizationId,
    workflow_definition_id      AS WorkflowDefinitionId,
    workflow_version            AS WorkflowVersion,
    entity_type                 AS EntityType,
    entity_id                   AS EntityId,
    status                      AS Status,
    current_step_id             AS CurrentStepId,
    current_step_execution_id   AS CurrentStepExecutionId,
    conversation_id             AS ConversationId,
    context                     AS Context,
    created_at                  AS CreatedAt,
    updated_at                  AS UpdatedAt,
    started_at                  AS StartedAt,
    waiting_since               AS WaitingSince,
    resume_at                   AS ResumeAt,
    completed_at                AS CompletedAt,
    cancelled_at                AS CancelledAt,
    failed_at                   AS FailedAt,
    failure_reason              AS FailureReason,
    revision                    AS Revision
FROM dbo.nh_wf_executions
WHERE status = 'Waiting'
  AND resume_at IS NOT NULL
  AND resume_at <= @AsOf
ORDER BY resume_at ASC;";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        var rows = await connection.QueryAsync<WorkflowExecutionRecord>(
            scope.CreateCommand(
                sql,
                new { AsOf = asOf, Limit = take },
                cancellationToken));

        return rows.Select(WorkflowExecutionMapper.FromRecord).ToList();
    }

    /// <summary>
    /// Finds a Waiting WhatsApp execution for <paramref name="organizationId"/> + phone correlation.
    /// Never matches across tenants.
    /// </summary>
    public async Task<(WorkflowExecution Execution, string WaitingEventType, string CorrelationId)?>
        FindWaitingByOrganizationAndPhoneAsync(
            Guid organizationId,
            string phone,
            string? eventType = null,
            CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty)
            throw new ArgumentException("OrganizationId is required.", nameof(organizationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(phone);

        var normalized = WhatsAppTenantResolver.NormalizePhone(phone);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("Phone is required.", nameof(phone));

        var candidates = new[]
        {
            normalized,
            $"whatsapp:{normalized}",
            WhatsAppTenantResolver.BuildConversationId(organizationId, normalized)
        };

        foreach (var correlationId in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var match = await FindWaitingByCorrelationAsync(
                correlationId,
                eventType,
                cancellationToken,
                organizationId);

            if (match is not null)
                return (match.Value.Execution, match.Value.WaitingEventType, correlationId);
        }

        return null;
    }

    /// <summary>
    /// Finds a Waiting email execution for <paramref name="organizationId"/> + prospect email.
    /// Matches step correlation id or execution conversation id. Never matches across tenants.
    /// </summary>
    public async Task<(WorkflowExecution Execution, string WaitingEventType, string CorrelationId)?>
        FindWaitingByOrganizationAndEmailAsync(
            Guid organizationId,
            string email,
            string? eventType = null,
            CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty)
            throw new ArgumentException("OrganizationId is required.", nameof(organizationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var normalized = EmailTenantResolver.NormalizeEmail(email);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("Email is required.", nameof(email));

        var candidates = new[]
        {
            normalized,
            EmailTenantResolver.BuildConversationId(organizationId, normalized)
        };

        foreach (var correlationId in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var match = await FindWaitingByCorrelationAsync(
                correlationId,
                eventType,
                cancellationToken,
                organizationId);

            if (match is not null)
                return (match.Value.Execution, match.Value.WaitingEventType, correlationId);

            var byConversation = await FindWaitingByConversationIdAsync(
                organizationId,
                correlationId,
                eventType,
                cancellationToken);

            if (byConversation is not null)
                return (
                    byConversation.Value.Execution,
                    byConversation.Value.WaitingEventType,
                    byConversation.Value.CorrelationId);
        }

        return null;
    }

    /// <summary>
    /// Finds a Waiting execution whose conversation id matches (tenant-scoped).
    /// </summary>
    public async Task<(WorkflowExecution Execution, string WaitingEventType, string CorrelationId)?> FindWaitingByConversationIdAsync(
        Guid organizationId,
        string conversationId,
        string? eventType,
        CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty)
            throw new ArgumentException("OrganizationId is required.", nameof(organizationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        const string sql = @"
SELECT TOP (1)
    e.id                          AS Id,
    e.organization_id             AS OrganizationId,
    e.workflow_definition_id      AS WorkflowDefinitionId,
    e.workflow_version            AS WorkflowVersion,
    e.entity_type                 AS EntityType,
    e.entity_id                   AS EntityId,
    e.status                      AS Status,
    e.current_step_id             AS CurrentStepId,
    e.current_step_execution_id   AS CurrentStepExecutionId,
    e.conversation_id             AS ConversationId,
    e.context                     AS Context,
    e.created_at                  AS CreatedAt,
    e.updated_at                  AS UpdatedAt,
    e.started_at                  AS StartedAt,
    e.waiting_since               AS WaitingSince,
    e.resume_at                   AS ResumeAt,
    e.completed_at                AS CompletedAt,
    e.cancelled_at                AS CancelledAt,
    e.failed_at                   AS FailedAt,
    e.failure_reason              AS FailureReason,
    e.revision                    AS Revision,
    se.waiting_event_type         AS WaitingEventType,
    se.correlation_id             AS StepCorrelationId
FROM dbo.nh_wf_executions e
INNER JOIN dbo.nh_wf_step_executions se
    ON se.id = e.current_step_execution_id
   AND se.workflow_execution_id = e.id
WHERE e.status = 'Waiting'
  AND se.status = 'Waiting'
  AND e.organization_id = @OrganizationId
  AND e.conversation_id = @ConversationId
  AND (@EventType IS NULL OR se.waiting_event_type = @EventType)
ORDER BY se.created_at DESC;";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        var row = await connection.QuerySingleOrDefaultAsync<WaitingByCorrelationRow>(
            scope.CreateCommand(
                sql,
                new
                {
                    OrganizationId = organizationId,
                    ConversationId = conversationId,
                    EventType = eventType
                },
                cancellationToken));

        if (row is null)
            return null;

        var record = new WorkflowExecutionRecord
        {
            Id = row.Id,
            OrganizationId = row.OrganizationId,
            WorkflowDefinitionId = row.WorkflowDefinitionId,
            WorkflowVersion = row.WorkflowVersion,
            EntityType = row.EntityType,
            EntityId = row.EntityId,
            Status = row.Status,
            CurrentStepId = row.CurrentStepId,
            CurrentStepExecutionId = row.CurrentStepExecutionId,
            ConversationId = row.ConversationId,
            Context = row.Context,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt,
            StartedAt = row.StartedAt,
            WaitingSince = row.WaitingSince,
            ResumeAt = row.ResumeAt,
            CompletedAt = row.CompletedAt,
            CancelledAt = row.CancelledAt,
            FailedAt = row.FailedAt,
            FailureReason = row.FailureReason,
            Revision = row.Revision
        };

        var execution = WorkflowExecutionMapper.FromRecord(record);
        var stepCorrelation = string.IsNullOrWhiteSpace(row.StepCorrelationId)
            ? conversationId
            : row.StepCorrelationId;
        return (execution, row.WaitingEventType ?? string.Empty, stepCorrelation);
    }

    /// <summary>
    /// Finds a Waiting execution whose current step waits on the given correlation id.
    /// When <paramref name="organizationId"/> is provided, results are tenant-scoped.
    /// </summary>
    public async Task<(WorkflowExecution Execution, string WaitingEventType)?> FindWaitingByCorrelationAsync(
        string correlationId,
        string? eventType,
        CancellationToken cancellationToken = default,
        Guid? organizationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        const string sql = @"
SELECT TOP (1)
    e.id                          AS Id,
    e.organization_id             AS OrganizationId,
    e.workflow_definition_id      AS WorkflowDefinitionId,
    e.workflow_version            AS WorkflowVersion,
    e.entity_type                 AS EntityType,
    e.entity_id                   AS EntityId,
    e.status                      AS Status,
    e.current_step_id             AS CurrentStepId,
    e.current_step_execution_id   AS CurrentStepExecutionId,
    e.conversation_id             AS ConversationId,
    e.context                     AS Context,
    e.created_at                  AS CreatedAt,
    e.updated_at                  AS UpdatedAt,
    e.started_at                  AS StartedAt,
    e.waiting_since               AS WaitingSince,
    e.resume_at                   AS ResumeAt,
    e.completed_at                AS CompletedAt,
    e.cancelled_at                AS CancelledAt,
    e.failed_at                   AS FailedAt,
    e.failure_reason              AS FailureReason,
    e.revision                    AS Revision,
    se.waiting_event_type         AS WaitingEventType
FROM dbo.nh_wf_executions e
INNER JOIN dbo.nh_wf_step_executions se
    ON se.id = e.current_step_execution_id
   AND se.workflow_execution_id = e.id
WHERE e.status = 'Waiting'
  AND se.status = 'Waiting'
  AND se.correlation_id = @CorrelationId
  AND (@EventType IS NULL OR se.waiting_event_type = @EventType)
  AND (@OrganizationId IS NULL OR e.organization_id = @OrganizationId)
ORDER BY se.created_at DESC;";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        var row = await connection.QuerySingleOrDefaultAsync<WaitingByCorrelationRow>(
            scope.CreateCommand(
                sql,
                new
                {
                    CorrelationId = correlationId,
                    EventType = eventType,
                    OrganizationId = organizationId
                },
                cancellationToken));

        if (row is null)
            return null;

        var record = new WorkflowExecutionRecord
        {
            Id = row.Id,
            OrganizationId = row.OrganizationId,
            WorkflowDefinitionId = row.WorkflowDefinitionId,
            WorkflowVersion = row.WorkflowVersion,
            EntityType = row.EntityType,
            EntityId = row.EntityId,
            Status = row.Status,
            CurrentStepId = row.CurrentStepId,
            CurrentStepExecutionId = row.CurrentStepExecutionId,
            ConversationId = row.ConversationId,
            Context = row.Context,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt,
            StartedAt = row.StartedAt,
            WaitingSince = row.WaitingSince,
            ResumeAt = row.ResumeAt,
            CompletedAt = row.CompletedAt,
            CancelledAt = row.CancelledAt,
            FailedAt = row.FailedAt,
            FailureReason = row.FailureReason,
            Revision = row.Revision
        };

        var execution = WorkflowExecutionMapper.FromRecord(record);
        return (execution, row.WaitingEventType ?? string.Empty);
    }
    private sealed class WaitingByCorrelationRow
    {
        public Guid Id { get; set; }
        public Guid OrganizationId { get; set; }
        public Guid WorkflowDefinitionId { get; set; }
        public int WorkflowVersion { get; set; }
        public string EntityType { get; set; } = string.Empty;
        public string EntityId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public Guid? CurrentStepId { get; set; }
        public Guid? CurrentStepExecutionId { get; set; }
        public string? ConversationId { get; set; }
        public string Context { get; set; } = "{}";
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? WaitingSince { get; set; }
        public DateTimeOffset? ResumeAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public DateTimeOffset? CancelledAt { get; set; }
        public DateTimeOffset? FailedAt { get; set; }
        public string? FailureReason { get; set; }
        public int Revision { get; set; }
        public string? WaitingEventType { get; set; }
        public string? StepCorrelationId { get; set; }
    }

    private static object ToWriteParameters(WorkflowExecution execution, DateTimeOffset? updatedAt) =>
        new
        {
            execution.Id,
            execution.OrganizationId,
            execution.WorkflowDefinitionId,
            execution.WorkflowVersion,
            execution.EntityType,
            execution.EntityId,
            Status = execution.Status.ToString(),
            execution.CurrentStepId,
            execution.CurrentStepExecutionId,
            execution.ConversationId,
            Context = WorkflowExecutionMapper.SerializeContext(execution.Context),
            execution.CreatedAt,
            UpdatedAt = updatedAt,
            execution.StartedAt,
            execution.WaitingSince,
            execution.ResumeAt,
            execution.CompletedAt,
            execution.CancelledAt,
            execution.FailedAt,
            execution.FailureReason,
            execution.Revision
        };
}
