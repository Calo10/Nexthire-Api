using System.Text.Json;
using nexthire_api.WorkflowAutomation.DTOs;
using nexthire_api.WorkflowAutomation.Models;
using Nexa.WorkflowAutomation.Enums;
using Nexa.WorkflowAutomation.Models.Definitions;
using Nexa.WorkflowAutomation.Models.Executions;
using Nexa.WorkflowAutomation.Models.Variables;
using Nexa.WorkflowAutomation.Serialization;

namespace nexthire_api.WorkflowAutomation.Mapping;

internal static class WorkflowExecutionMapper
{
    public static WorkflowExecution CreatePending(
        Guid organizationId,
        Guid executionId,
        WorkflowDefinition definition,
        string entityType,
        string entityId,
        DateTimeOffset now,
        IReadOnlyDictionary<string, JsonElement>? initialVariables = null)
    {
        var normalizedEntityType = entityType.Trim();
        var normalizedEntityId = entityId.Trim();

        var execution = new WorkflowExecution
        {
            Id = executionId,
            OrganizationId = organizationId,
            WorkflowDefinitionId = definition.Id,
            WorkflowVersion = definition.Version,
            EntityType = normalizedEntityType,
            EntityId = normalizedEntityId,
            Status = WorkflowExecutionStatus.Pending,
            CurrentStepId = definition.StartStepId,
            CurrentStepExecutionId = null,
            ConversationId = null,
            CreatedAt = now,
            StartedAt = null,
            WaitingSince = null,
            ResumeAt = null,
            CompletedAt = null,
            CancelledAt = null,
            FailedAt = null,
            FailureReason = null,
            Revision = 0,
            Context = new WorkflowExecutionContext
            {
                OrganizationId = organizationId,
                EntityType = normalizedEntityType,
                EntityId = normalizedEntityId,
                ConversationId = null,
                Variables = new WorkflowVariableCollection(),
                Metadata = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            }
        };

        execution.Context.Variables.Merge(initialVariables, WorkflowVariableScope.Execution, now);
        return execution;
    }

    public static WorkflowExecutionResponse ToResponse(WorkflowExecution execution, DateTimeOffset? updatedAt = null) =>
        new()
        {
            Id = execution.Id,
            OrganizationId = execution.OrganizationId,
            WorkflowDefinitionId = execution.WorkflowDefinitionId,
            WorkflowVersion = execution.WorkflowVersion,
            EntityType = execution.EntityType,
            EntityId = execution.EntityId,
            Status = execution.Status.ToString(),
            CurrentStepId = execution.CurrentStepId,
            CurrentStepExecutionId = execution.CurrentStepExecutionId,
            ConversationId = execution.ConversationId,
            CreatedAt = execution.CreatedAt,
            UpdatedAt = updatedAt,
            StartedAt = execution.StartedAt,
            WaitingSince = execution.WaitingSince,
            ResumeAt = execution.ResumeAt,
            CompletedAt = execution.CompletedAt,
            CancelledAt = execution.CancelledAt,
            FailedAt = execution.FailedAt,
            FailureReason = execution.FailureReason,
            Revision = execution.Revision,
            Variables = ToVariablesDictionary(execution.Context),
            Metadata = ToMetadataDictionary(execution.Context)
        };

    public static WorkflowExecutionHistoryItemResponse ToHistoryItem(WorkflowHistoryEntry entry) =>
        new()
        {
            Id = entry.Id,
            EventType = entry.EventType.ToString(),
            OccurredAt = entry.OccurredAt,
            Message = entry.Message,
            Data = entry.Data
        };

    public static WorkflowExecutionStepItemResponse ToStepItem(WorkflowStepExecution step) =>
        new()
        {
            Id = step.Id,
            StepDefinitionId = step.StepDefinitionId,
            ActivityType = step.ActivityType,
            Status = step.Status.ToString(),
            Attempt = step.Attempt,
            StartedAt = step.StartedAt,
            WaitingSince = step.WaitingSince,
            ResumeAt = step.ResumeAt,
            CompletedAt = step.CompletedAt,
            WaitingEventType = step.WaitingEventType,
            CorrelationId = step.CorrelationId,
            ExternalReferenceId = step.ExternalReferenceId,
            Input = step.Input,
            Output = step.Output,
            ErrorCode = step.ErrorCode,
            ErrorMessage = step.ErrorMessage,
            Metadata = step.Metadata is { Count: > 0 }
                ? new Dictionary<string, JsonElement>(step.Metadata, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        };

    public static WorkflowExecutionEventReceiptResponse ToEventReceiptItem(
        Guid eventId,
        string? eventType,
        string? correlationId,
        DateTimeOffset processedAt) =>
        new()
        {
            EventId = eventId,
            EventType = string.IsNullOrWhiteSpace(eventType) ? null : eventType,
            CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? null : correlationId,
            ProcessedAt = processedAt
        };

    private static IReadOnlyDictionary<string, JsonElement> ToVariablesDictionary(
        WorkflowExecutionContext? context)
    {
        if (context?.Variables is null)
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        return context.Variables.AsReadOnly()
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Value,
                StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, JsonElement> ToMetadataDictionary(
        WorkflowExecutionContext? context)
    {
        if (context?.Metadata is null || context.Metadata.Count == 0)
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        return new Dictionary<string, JsonElement>(context.Metadata, StringComparer.OrdinalIgnoreCase);
    }

    public static WorkflowExecutionListItemResponse ToListItem(WorkflowExecutionRecord row) =>
        new()
        {
            Id = row.Id,
            WorkflowDefinitionId = row.WorkflowDefinitionId,
            EntityType = row.EntityType,
            EntityId = row.EntityId,
            Status = row.Status,
            CurrentStepId = row.CurrentStepId,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt,
            Revision = row.Revision
        };

    public static string SerializeContext(WorkflowExecutionContext context)
    {
        var document = new WorkflowExecutionContextDocument
        {
            OrganizationId = context.OrganizationId,
            EntityType = context.EntityType,
            EntityId = context.EntityId,
            ConversationId = context.ConversationId,
            Variables = context.Variables.AsReadOnly()
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Value,
                    StringComparer.OrdinalIgnoreCase),
            Metadata = context.Metadata is null
                ? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, JsonElement>(context.Metadata, StringComparer.OrdinalIgnoreCase)
        };

        return WorkflowJsonSerializer.Serialize(document);
    }

    public static WorkflowExecution FromRecord(WorkflowExecutionRecord row)
    {
        if (!Enum.TryParse<WorkflowExecutionStatus>(row.Status, ignoreCase: true, out var status))
            status = WorkflowExecutionStatus.Pending;

        return new WorkflowExecution
        {
            Id = row.Id,
            OrganizationId = row.OrganizationId,
            WorkflowDefinitionId = row.WorkflowDefinitionId,
            WorkflowVersion = row.WorkflowVersion,
            EntityType = row.EntityType,
            EntityId = row.EntityId,
            Status = status,
            CurrentStepId = row.CurrentStepId,
            CurrentStepExecutionId = row.CurrentStepExecutionId,
            ConversationId = row.ConversationId,
            CreatedAt = row.CreatedAt,
            StartedAt = row.StartedAt,
            WaitingSince = row.WaitingSince,
            ResumeAt = row.ResumeAt,
            CompletedAt = row.CompletedAt,
            CancelledAt = row.CancelledAt,
            FailedAt = row.FailedAt,
            FailureReason = row.FailureReason,
            Revision = row.Revision,
            Context = DeserializeContext(row.Context, row.OrganizationId, row.EntityType, row.EntityId, row.ConversationId)
        };
    }

    private static WorkflowExecutionContext DeserializeContext(
        string json,
        Guid organizationId,
        string entityType,
        string entityId,
        string? conversationId)
    {
        var document = string.IsNullOrWhiteSpace(json)
            ? null
            : WorkflowJsonSerializer.Deserialize<WorkflowExecutionContextDocument>(json);

        var variables = new WorkflowVariableCollection();
        if (document?.Variables is not null)
        {
            foreach (var pair in document.Variables)
            {
                variables.Set(pair.Key, pair.Value, WorkflowVariableScope.Execution);
            }
        }

        return new WorkflowExecutionContext
        {
            OrganizationId = document?.OrganizationId is { } org && org != Guid.Empty ? org : organizationId,
            EntityType = string.IsNullOrWhiteSpace(document?.EntityType) ? entityType : document.EntityType,
            EntityId = string.IsNullOrWhiteSpace(document?.EntityId) ? entityId : document.EntityId,
            ConversationId = document?.ConversationId ?? conversationId,
            Variables = variables,
            Metadata = document?.Metadata
                ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        };
    }
}
