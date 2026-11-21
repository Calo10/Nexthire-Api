using System.Text.Json;

namespace nexthire_api.WorkflowAutomation.DTOs;

public sealed class ProcessWorkflowEventRequest
{
    public required string EventType { get; set; }
    public JsonElement? Payload { get; set; }
    public string? CorrelationId { get; set; }
}
