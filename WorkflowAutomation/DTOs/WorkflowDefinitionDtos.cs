using System.Text.Json;
using nexthire_api.WorkflowAutomation.Models;
using Nexa.WorkflowAutomation.Models.Definitions;

namespace nexthire_api.WorkflowAutomation.DTOs;

public sealed class CreateWorkflowDefinitionRequest
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public Guid StartStepId { get; set; }
    public WorkflowTrigger? Trigger { get; set; }
    public IReadOnlyList<WorkflowStepDefinition> Steps { get; set; } =
        Array.Empty<WorkflowStepDefinition>();
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; set; }
}

public sealed class UpdateWorkflowDefinitionRequest
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public Guid StartStepId { get; set; }
    public WorkflowTrigger? Trigger { get; set; }
    public IReadOnlyList<WorkflowStepDefinition> Steps { get; set; } =
        Array.Empty<WorkflowStepDefinition>();
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; set; }
}

public sealed class WorkflowDefinitionResponse
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public int Version { get; set; }
    public required string Status { get; set; }
    public Guid StartStepId { get; set; }
    public required WorkflowTrigger Trigger { get; set; }
    public IReadOnlyList<WorkflowStepDefinition> Steps { get; set; } =
        Array.Empty<WorkflowStepDefinition>();
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}

public sealed class WorkflowDefinitionListItemResponse
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public int Version { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}
