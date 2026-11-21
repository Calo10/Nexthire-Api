using System.Text.Json;
using nexthire_api.WorkflowAutomation.Models;
using Nexa.WorkflowAutomation.Models.Definitions;
using Nexa.WorkflowAutomation.Serialization;

namespace nexthire_api.WorkflowAutomation;

/// <summary>
/// Stores trigger data on <see cref="WorkflowDefinition.Metadata"/> because
/// Nexa.WorkflowAutomation 0.1.1864 has no <c>WorkflowDefinition.Trigger</c>.
/// </summary>
internal static class WorkflowDefinitionTriggers
{
    internal const string MetadataKey = "__wf.trigger";

    public static WorkflowTrigger Get(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Read(definition.Metadata);
    }

    public static WorkflowTrigger Read(IReadOnlyDictionary<string, JsonElement>? metadata)
    {
        if (metadata is not null &&
            metadata.TryGetValue(MetadataKey, out var element) &&
            element.ValueKind is JsonValueKind.Object or JsonValueKind.String)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var type = element.GetString();
                if (!string.IsNullOrWhiteSpace(type))
                    return new WorkflowTrigger { Type = type };
            }
            else
            {
                var parsed = WorkflowJsonSerializer.Deserialize<WorkflowTrigger>(element);
                if (parsed is not null && !string.IsNullOrWhiteSpace(parsed.Type))
                    return parsed;
            }
        }

        return new WorkflowTrigger { Type = "manual" };
    }

    public static IReadOnlyDictionary<string, JsonElement> With(
        IReadOnlyDictionary<string, JsonElement>? metadata,
        WorkflowTrigger? trigger)
    {
        var resolved = trigger ?? new WorkflowTrigger { Type = "manual" };
        var copy = metadata is null
            ? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, JsonElement>(metadata, StringComparer.OrdinalIgnoreCase);
        copy[MetadataKey] = WorkflowJsonSerializer.SerializeToElement(resolved);
        return copy;
    }

    public static IReadOnlyDictionary<string, JsonElement>? WithoutTrigger(
        IReadOnlyDictionary<string, JsonElement>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return metadata;

        if (!metadata.ContainsKey(MetadataKey))
            return metadata;

        var copy = new Dictionary<string, JsonElement>(metadata, StringComparer.OrdinalIgnoreCase);
        copy.Remove(MetadataKey);
        return copy.Count == 0 ? null : copy;
    }
}
