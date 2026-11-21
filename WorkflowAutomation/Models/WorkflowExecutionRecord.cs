using System.Text.Json;

namespace nexthire_api.WorkflowAutomation.Models;

public sealed class WorkflowExecutionRecord
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
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? WaitingSince { get; set; }
    public DateTimeOffset? ResumeAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public DateTimeOffset? FailedAt { get; set; }
    public string? FailureReason { get; set; }
    public int Revision { get; set; }
}

public sealed class WorkflowExecutionContextDocument
{
    public Guid OrganizationId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string? ConversationId { get; set; }
    public Dictionary<string, JsonElement> Variables { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, JsonElement> Metadata { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
