namespace nexthire_api.WorkflowAutomation.Models;

/// <summary>
/// NextHire-owned trigger descriptor. The published Nexa.WorkflowAutomation
/// package does not yet expose this type on <c>WorkflowDefinition</c>.
/// </summary>
public sealed class WorkflowTrigger
{
    public required string Type { get; set; }

    public Dictionary<string, object>? Configuration { get; set; }
}
