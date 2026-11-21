using System.Text.Json;

namespace nexthire_api.WorkflowAutomation.DTOs;

public sealed class CreateWorkflowExecutionRequest
{
    public Guid WorkflowDefinitionId { get; set; }
    public required string EntityType { get; set; }
    public required string EntityId { get; set; }
    public IReadOnlyDictionary<string, JsonElement>? InitialVariables { get; set; }
}

public sealed class WorkflowExecutionResponse
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkflowDefinitionId { get; set; }
    public int WorkflowVersion { get; set; }
    public required string EntityType { get; set; }
    public required string EntityId { get; set; }
    public required string Status { get; set; }
    public Guid? CurrentStepId { get; set; }
    public Guid? CurrentStepExecutionId { get; set; }
    public string? ConversationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? WaitingSince { get; set; }
    public DateTimeOffset? ResumeAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public DateTimeOffset? FailedAt { get; set; }
    public string? FailureReason { get; set; }
    public int Revision { get; set; }

    /// <summary>Execution variables from <c>WorkflowExecution.Context.Variables</c>.</summary>
    public IReadOnlyDictionary<string, JsonElement> Variables { get; set; } =
        new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Execution metadata from <c>WorkflowExecution.Context.Metadata</c>.</summary>
    public IReadOnlyDictionary<string, JsonElement> Metadata { get; set; } =
        new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
}

public sealed class WorkflowExecutionListItemResponse
{
    public Guid Id { get; set; }
    public Guid WorkflowDefinitionId { get; set; }
    public required string EntityType { get; set; }
    public required string EntityId { get; set; }
    public required string Status { get; set; }
    public Guid? CurrentStepId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public int Revision { get; set; }
}

public sealed class WorkflowExecutionHistoryItemResponse
{
    public Guid Id { get; set; }
    public required string EventType { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public required string Message { get; set; }
    public JsonElement? Data { get; set; }
}

public sealed class WorkflowExecutionStepItemResponse
{
    public Guid Id { get; set; }
    public Guid StepDefinitionId { get; set; }
    public required string ActivityType { get; set; }
    public required string Status { get; set; }
    public int Attempt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? WaitingSince { get; set; }
    public DateTimeOffset? ResumeAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? WaitingEventType { get; set; }
    public string? CorrelationId { get; set; }
    public string? ExternalReferenceId { get; set; }
    public JsonElement? Input { get; set; }
    public JsonElement? Output { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public IReadOnlyDictionary<string, JsonElement> Metadata { get; set; } =
        new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
}

public sealed class WorkflowExecutionEventReceiptResponse
{
    public Guid EventId { get; set; }
    public string? EventType { get; set; }
    public string? CorrelationId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}
