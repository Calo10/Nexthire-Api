using Dapper;
using nexthire_api.WorkflowAutomation.DTOs;
using Nexa.WorkflowAutomation.Contracts.Events;

namespace nexthire_api.WorkflowAutomation.Repositories;

public sealed class SqlWorkflowEventReceiptRepository : IWorkflowEventReceiptRepository
{
    private readonly IWorkflowSqlSession _sql;

    public SqlWorkflowEventReceiptRepository(IWorkflowSqlSession sql)
    {
        _sql = sql;
    }

    public async Task<bool> HasBeenProcessedAsync(
        Guid organizationId,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT CASE WHEN EXISTS (
    SELECT 1
    FROM dbo.nh_wf_event_receipts
    WHERE organization_id = @OrganizationId
      AND event_id = @EventId
) THEN 1 ELSE 0 END;";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        var exists = await connection.ExecuteScalarAsync<int>(
            scope.CreateCommand(
                sql,
                new { OrganizationId = organizationId, EventId = eventId },
                cancellationToken));

        return exists == 1;
    }

    /// <summary>
    /// Dashboard read: event receipts for an execution (persisted columns only).
    /// Ordered by <c>processed_at ASC</c>.
    /// </summary>
    public async Task<IReadOnlyList<WorkflowExecutionEventReceiptResponse>> ListByExecutionIdAsync(
        Guid organizationId,
        Guid workflowExecutionId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT
    event_id        AS EventId,
    event_type      AS EventType,
    correlation_id  AS CorrelationId,
    processed_at    AS ProcessedAt
FROM dbo.nh_wf_event_receipts
WHERE organization_id = @OrganizationId
  AND workflow_execution_id = @WorkflowExecutionId
ORDER BY processed_at ASC;";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        var rows = await connection.QueryAsync<EventReceiptRow>(
            scope.CreateCommand(
                sql,
                new { OrganizationId = organizationId, WorkflowExecutionId = workflowExecutionId },
                cancellationToken));

        return rows.Select(row => new WorkflowExecutionEventReceiptResponse
        {
            EventId = row.EventId,
            EventType = string.IsNullOrWhiteSpace(row.EventType) ? null : row.EventType,
            CorrelationId = string.IsNullOrWhiteSpace(row.CorrelationId) ? null : row.CorrelationId,
            ProcessedAt = row.ProcessedAt
        }).ToList();
    }

    public async Task MarkProcessedAsync(
        Guid organizationId,
        Guid eventId,
        Guid workflowExecutionId,
        DateTimeOffset processedAt,
        CancellationToken cancellationToken)
    {
        // Schema requires event_type; the package contract does not supply it.
        // Idempotency is enforced by PK (organization_id, event_id).
        const string sql = @"
IF NOT EXISTS (
    SELECT 1
    FROM dbo.nh_wf_event_receipts
    WHERE organization_id = @OrganizationId
      AND event_id = @EventId
)
BEGIN
    INSERT INTO dbo.nh_wf_event_receipts (
        organization_id,
        event_id,
        workflow_execution_id,
        event_type,
        correlation_id,
        processed_at
    )
    VALUES (
        @OrganizationId,
        @EventId,
        @WorkflowExecutionId,
        N'',
        NULL,
        @ProcessedAt
    );
END;";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        await connection.ExecuteAsync(
            scope.CreateCommand(
                sql,
                new
                {
                    OrganizationId = organizationId,
                    EventId = eventId,
                    WorkflowExecutionId = workflowExecutionId,
                    ProcessedAt = processedAt
                },
                cancellationToken));
    }

    private sealed class EventReceiptRow
    {
        public Guid EventId { get; set; }
        public string? EventType { get; set; }
        public string? CorrelationId { get; set; }
        public DateTimeOffset ProcessedAt { get; set; }
    }
}
