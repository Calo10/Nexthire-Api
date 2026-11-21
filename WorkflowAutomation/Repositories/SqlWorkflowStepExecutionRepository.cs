using System.Text.Json;
using Dapper;
using Nexa.WorkflowAutomation.Contracts.Persistence;
using Nexa.WorkflowAutomation.Enums;
using Nexa.WorkflowAutomation.Models.Executions;
using Nexa.WorkflowAutomation.Serialization;

namespace nexthire_api.WorkflowAutomation.Repositories;

public sealed class SqlWorkflowStepExecutionRepository : IWorkflowStepExecutionRepository
{
    private readonly IWorkflowSqlSession _sql;

    public SqlWorkflowStepExecutionRepository(IWorkflowSqlSession sql)
    {
        _sql = sql;
    }

    public async Task<WorkflowStepExecution?> GetByIdAsync(
        Guid organizationId,
        Guid id,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT
    id                      AS Id,
    workflow_execution_id   AS WorkflowExecutionId,
    step_definition_id      AS StepDefinitionId,
    activity_type           AS ActivityType,
    status                  AS Status,
    attempt                 AS Attempt,
    created_at              AS CreatedAt,
    started_at              AS StartedAt,
    waiting_since           AS WaitingSince,
    resume_at               AS ResumeAt,
    completed_at            AS CompletedAt,
    waiting_event_type      AS WaitingEventType,
    correlation_id          AS CorrelationId,
    external_reference_id   AS ExternalReferenceId,
    input                   AS InputJson,
    output                  AS OutputJson,
    metadata                AS MetadataJson,
    error_code              AS ErrorCode,
    error_message           AS ErrorMessage
FROM dbo.nh_wf_step_executions
WHERE organization_id = @OrganizationId
  AND id = @Id;";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        var row = await connection.QuerySingleOrDefaultAsync<StepExecutionRow>(
            scope.CreateCommand(
                sql,
                new { OrganizationId = organizationId, Id = id },
                cancellationToken));

        return row is null ? null : Map(row);
    }

    public async Task<WorkflowStepExecution?> GetCurrentAsync(
        Guid organizationId,
        Guid workflowExecutionId,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT TOP (1)
    se.id                      AS Id,
    se.workflow_execution_id   AS WorkflowExecutionId,
    se.step_definition_id      AS StepDefinitionId,
    se.activity_type           AS ActivityType,
    se.status                  AS Status,
    se.attempt                 AS Attempt,
    se.created_at              AS CreatedAt,
    se.started_at              AS StartedAt,
    se.waiting_since           AS WaitingSince,
    se.resume_at               AS ResumeAt,
    se.completed_at            AS CompletedAt,
    se.waiting_event_type      AS WaitingEventType,
    se.correlation_id          AS CorrelationId,
    se.external_reference_id   AS ExternalReferenceId,
    se.input                   AS InputJson,
    se.output                  AS OutputJson,
    se.metadata                AS MetadataJson,
    se.error_code              AS ErrorCode,
    se.error_message           AS ErrorMessage
FROM dbo.nh_wf_step_executions se
INNER JOIN dbo.nh_wf_executions e
    ON e.id = se.workflow_execution_id
   AND e.organization_id = se.organization_id
WHERE se.organization_id = @OrganizationId
  AND se.workflow_execution_id = @WorkflowExecutionId
  AND (
        e.current_step_execution_id = se.id
        OR e.current_step_execution_id IS NULL
      )
ORDER BY
    CASE WHEN e.current_step_execution_id = se.id THEN 0 ELSE 1 END,
    se.created_at DESC;";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        var row = await connection.QuerySingleOrDefaultAsync<StepExecutionRow>(
            scope.CreateCommand(
                sql,
                new { OrganizationId = organizationId, WorkflowExecutionId = workflowExecutionId },
                cancellationToken));

        return row is null ? null : Map(row);
    }

    /// <summary>
    /// Dashboard read: all step attempts for an execution, ordered by <c>created_at ASC</c>.
    /// </summary>
    public async Task<IReadOnlyList<WorkflowStepExecution>> ListByExecutionIdAsync(
        Guid organizationId,
        Guid workflowExecutionId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT
    id                      AS Id,
    workflow_execution_id   AS WorkflowExecutionId,
    step_definition_id      AS StepDefinitionId,
    activity_type           AS ActivityType,
    status                  AS Status,
    attempt                 AS Attempt,
    created_at              AS CreatedAt,
    started_at              AS StartedAt,
    waiting_since           AS WaitingSince,
    resume_at               AS ResumeAt,
    completed_at            AS CompletedAt,
    waiting_event_type      AS WaitingEventType,
    correlation_id          AS CorrelationId,
    external_reference_id   AS ExternalReferenceId,
    input                   AS InputJson,
    output                  AS OutputJson,
    metadata                AS MetadataJson,
    error_code              AS ErrorCode,
    error_message           AS ErrorMessage
FROM dbo.nh_wf_step_executions
WHERE organization_id = @OrganizationId
  AND workflow_execution_id = @WorkflowExecutionId
ORDER BY created_at ASC;";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        var rows = await connection.QueryAsync<StepExecutionRow>(
            scope.CreateCommand(
                sql,
                new { OrganizationId = organizationId, WorkflowExecutionId = workflowExecutionId },
                cancellationToken));

        return rows.Select(Map).ToList();
    }

    public async Task AddAsync(WorkflowStepExecution stepExecution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stepExecution);

        const string lookupSql = @"
SELECT organization_id
FROM dbo.nh_wf_executions
WHERE id = @WorkflowExecutionId;";

        const string insertSql = @"
INSERT INTO dbo.nh_wf_step_executions (
    id,
    organization_id,
    workflow_execution_id,
    step_definition_id,
    activity_type,
    status,
    attempt,
    created_at,
    updated_at,
    started_at,
    waiting_since,
    resume_at,
    completed_at,
    waiting_event_type,
    correlation_id,
    external_reference_id,
    input,
    output,
    metadata,
    error_code,
    error_message
)
VALUES (
    @Id,
    @OrganizationId,
    @WorkflowExecutionId,
    @StepDefinitionId,
    @ActivityType,
    @Status,
    @Attempt,
    @CreatedAt,
    @UpdatedAt,
    @StartedAt,
    @WaitingSince,
    @ResumeAt,
    @CompletedAt,
    @WaitingEventType,
    @CorrelationId,
    @ExternalReferenceId,
    @Input,
    @Output,
    @Metadata,
    @ErrorCode,
    @ErrorMessage
);";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;

        var organizationId = await connection.ExecuteScalarAsync<Guid?>(
            scope.CreateCommand(lookupSql, new { stepExecution.WorkflowExecutionId }, cancellationToken));

        if (organizationId is null || organizationId == Guid.Empty)
        {
            throw new InvalidOperationException(
                $"Cannot add step execution: workflow execution '{stepExecution.WorkflowExecutionId}' was not found.");
        }

        await connection.ExecuteAsync(
            scope.CreateCommand(
                insertSql,
                ToParams(stepExecution, organizationId.Value),
                cancellationToken));
    }

    public async Task UpdateAsync(WorkflowStepExecution stepExecution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stepExecution);

        const string sql = @"
UPDATE dbo.nh_wf_step_executions
SET
    status                  = @Status,
    attempt                 = @Attempt,
    updated_at              = @UpdatedAt,
    started_at              = @StartedAt,
    waiting_since           = @WaitingSince,
    resume_at               = @ResumeAt,
    completed_at            = @CompletedAt,
    waiting_event_type      = @WaitingEventType,
    correlation_id          = @CorrelationId,
    external_reference_id   = @ExternalReferenceId,
    input                   = @Input,
    output                  = @Output,
    metadata                = @Metadata,
    error_code              = @ErrorCode,
    error_message           = @ErrorMessage
WHERE id = @Id
  AND workflow_execution_id = @WorkflowExecutionId;";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        await connection.ExecuteAsync(
            scope.CreateCommand(
                sql,
                ToParams(stepExecution, organizationId: Guid.Empty),
                cancellationToken));
    }

    private static object ToParams(WorkflowStepExecution step, Guid organizationId) =>
        new
        {
            step.Id,
            OrganizationId = organizationId,
            step.WorkflowExecutionId,
            step.StepDefinitionId,
            step.ActivityType,
            Status = step.Status.ToString(),
            step.Attempt,
            step.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
            step.StartedAt,
            step.WaitingSince,
            step.ResumeAt,
            step.CompletedAt,
            step.WaitingEventType,
            step.CorrelationId,
            step.ExternalReferenceId,
            Input = SerializeJson(step.Input),
            Output = SerializeJson(step.Output),
            Metadata = step.Metadata is { Count: > 0 }
                ? WorkflowJsonSerializer.Serialize(step.Metadata)
                : null,
            step.ErrorCode,
            step.ErrorMessage
        };

    private static string? SerializeJson(JsonElement? element) =>
        element is null ? null : element.Value.GetRawText();

    private static WorkflowStepExecution Map(StepExecutionRow row)
    {
        if (!Enum.TryParse<WorkflowStepExecutionStatus>(row.Status, ignoreCase: true, out var status))
            status = WorkflowStepExecutionStatus.Pending;

        return new WorkflowStepExecution
        {
            Id = row.Id,
            WorkflowExecutionId = row.WorkflowExecutionId,
            StepDefinitionId = row.StepDefinitionId,
            ActivityType = row.ActivityType,
            Status = status,
            Attempt = row.Attempt,
            CreatedAt = row.CreatedAt,
            StartedAt = row.StartedAt,
            WaitingSince = row.WaitingSince,
            ResumeAt = row.ResumeAt,
            CompletedAt = row.CompletedAt,
            WaitingEventType = row.WaitingEventType,
            CorrelationId = row.CorrelationId,
            ExternalReferenceId = row.ExternalReferenceId,
            Input = ParseJson(row.InputJson),
            Output = ParseJson(row.OutputJson),
            Metadata = string.IsNullOrWhiteSpace(row.MetadataJson)
                ? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                : WorkflowJsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.MetadataJson)
                  ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase),
            ErrorCode = row.ErrorCode,
            ErrorMessage = row.ErrorMessage
        };
    }

    private static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class StepExecutionRow
    {
        public Guid Id { get; set; }
        public Guid WorkflowExecutionId { get; set; }
        public Guid StepDefinitionId { get; set; }
        public string ActivityType { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int Attempt { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? WaitingSince { get; set; }
        public DateTimeOffset? ResumeAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string? WaitingEventType { get; set; }
        public string? CorrelationId { get; set; }
        public string? ExternalReferenceId { get; set; }
        public string? InputJson { get; set; }
        public string? OutputJson { get; set; }
        public string? MetadataJson { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
