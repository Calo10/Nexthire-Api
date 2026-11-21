using System.Text.Json;
using Nexa.WorkflowAutomation.Models.Definitions;

namespace nexthire_api.WorkflowAutomation.Models;

/// <summary>
/// Flat row shape for dbo.nh_wf_definitions (Dapper).
/// </summary>
public sealed class WorkflowDefinitionRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Version { get; set; }
    public string Status { get; set; } = string.Empty;
    public Guid StartStepId { get; set; }
    public string Definition { get; set; } = string.Empty;
    public string? Metadata { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>
/// JSON document stored in nh_wf_definitions.definition (full aggregate graph).
/// </summary>
public sealed class WorkflowDefinitionDocument
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Version { get; set; }
    public string Status { get; set; } = string.Empty;
    public Guid StartStepId { get; set; }
    public WorkflowTrigger? Trigger { get; set; }
    public IReadOnlyList<Nexa.WorkflowAutomation.Models.Definitions.WorkflowStepDefinition> Steps { get; set; } =
        Array.Empty<Nexa.WorkflowAutomation.Models.Definitions.WorkflowStepDefinition>();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public string? CreatedBy { get; set; }
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; set; }
}
