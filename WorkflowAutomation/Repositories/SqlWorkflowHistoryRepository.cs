using System.Text.Json;
using Dapper;
using nexthire_api.WorkflowAutomation.DTOs;
using Nexa.WorkflowAutomation.Contracts.Persistence;
using Nexa.WorkflowAutomation.Enums;
using Nexa.WorkflowAutomation.Models.Executions;
using Nexa.WorkflowAutomation.Serialization;

namespace nexthire_api.WorkflowAutomation.Repositories;

public sealed class SqlWorkflowHistoryRepository : IWorkflowHistoryRepository
{
    private readonly IWorkflowSqlSession _sql;

    public SqlWorkflowHistoryRepository(IWorkflowSqlSession sql)
    {
        _sql = sql;
    }

    public async Task AddAsync(WorkflowHistoryEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        const string lookupSql = @"
SELECT organization_id
FROM dbo.nh_wf_executions
WHERE id = @WorkflowExecutionId;";

        const string insertSql = @"
INSERT INTO dbo.nh_wf_history (
    id,
    organization_id,
    workflow_execution_id,
    event_type,
    occurred_at,
    message,
    data
)
VALUES (
    @Id,
    @OrganizationId,
    @WorkflowExecutionId,
    @EventType,
    @OccurredAt,
    @Message,
    @Data
);";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;

        var organizationId = await connection.ExecuteScalarAsync<Guid?>(
            scope.CreateCommand(lookupSql, new { entry.WorkflowExecutionId }, cancellationToken));

        if (organizationId is null || organizationId == Guid.Empty)
        {
            throw new InvalidOperationException(
                $"Cannot add history: workflow execution '{entry.WorkflowExecutionId}' was not found.");
        }

        await connection.ExecuteAsync(
            scope.CreateCommand(
                insertSql,
                new
                {
                    entry.Id,
                    OrganizationId = organizationId.Value,
                    entry.WorkflowExecutionId,
                    EventType = entry.EventType.ToString(),
                    entry.OccurredAt,
                    entry.Message,
                    Data = entry.Data is null ? null : entry.Data.Value.GetRawText()
                },
                cancellationToken));
    }

    public async Task<IReadOnlyList<WorkflowHistoryEntry>> GetByExecutionIdAsync(
        Guid organizationId,
        Guid workflowExecutionId,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT
    id                      AS Id,
    workflow_execution_id   AS WorkflowExecutionId,
    event_type              AS EventType,
    occurred_at             AS OccurredAt,
    message                 AS Message,
    data                    AS DataJson
FROM dbo.nh_wf_history
WHERE organization_id = @OrganizationId
  AND workflow_execution_id = @WorkflowExecutionId
ORDER BY occurred_at ASC;";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        var rows = await connection.QueryAsync<HistoryRow>(
            scope.CreateCommand(
                sql,
                new { OrganizationId = organizationId, WorkflowExecutionId = workflowExecutionId },
                cancellationToken));

        return rows.Select(Map).ToList();
    }

    /// <summary>
    /// Dashboard read: returns history rows with the persisted <c>event_type</c> string (ordered ASC).
    /// </summary>
    public async Task<IReadOnlyList<WorkflowExecutionHistoryItemResponse>> ListByExecutionIdAsync(
        Guid organizationId,
        Guid workflowExecutionId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT
    id                      AS Id,
    event_type              AS EventType,
    occurred_at             AS OccurredAt,
    message                 AS Message,
    data                    AS DataJson
FROM dbo.nh_wf_history
WHERE organization_id = @OrganizationId
  AND workflow_execution_id = @WorkflowExecutionId
ORDER BY occurred_at ASC;";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        var rows = await connection.QueryAsync<HistoryRow>(
            scope.CreateCommand(
                sql,
                new { OrganizationId = organizationId, WorkflowExecutionId = workflowExecutionId },
                cancellationToken));

        return rows.Select(ToDashboardItem).ToList();
    }

    private static WorkflowExecutionHistoryItemResponse ToDashboardItem(HistoryRow row)
    {
        JsonElement? data = null;
        if (!string.IsNullOrWhiteSpace(row.DataJson))
        {
            using var document = JsonDocument.Parse(row.DataJson);
            data = document.RootElement.Clone();
        }

        return new WorkflowExecutionHistoryItemResponse
        {
            Id = row.Id,
            EventType = row.EventType,
            OccurredAt = row.OccurredAt,
            Message = row.Message,
            Data = data
        };
    }

    private static WorkflowHistoryEntry Map(HistoryRow row)
    {
        if (!Enum.TryParse<WorkflowHistoryEventType>(row.EventType, ignoreCase: true, out var eventType))
            eventType = WorkflowHistoryEventType.StepStarted;

        JsonElement? data = null;
        if (!string.IsNullOrWhiteSpace(row.DataJson))
        {
            using var document = JsonDocument.Parse(row.DataJson);
            data = document.RootElement.Clone();
        }

        return new WorkflowHistoryEntry
        {
            Id = row.Id,
            WorkflowExecutionId = row.WorkflowExecutionId,
            EventType = eventType,
            OccurredAt = row.OccurredAt,
            Message = row.Message,
            Data = data
        };
    }

    private sealed class HistoryRow
    {
        public Guid Id { get; set; }
        public Guid WorkflowExecutionId { get; set; }
        public string EventType { get; set; } = string.Empty;
        public DateTimeOffset OccurredAt { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? DataJson { get; set; }
    }
}
