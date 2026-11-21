using System.Text.Json;
using nexthire_api.WorkflowAutomation.DTOs;
using nexthire_api.WorkflowAutomation.Models;
using Nexa.WorkflowAutomation.Enums;
using Nexa.WorkflowAutomation.Models.Definitions;
using Nexa.WorkflowAutomation.Serialization;

namespace nexthire_api.WorkflowAutomation.Mapping;

internal static class WorkflowDefinitionMapper
{
    public static WorkflowDefinition ToDomain(
        Guid organizationId,
        Guid id,
        CreateWorkflowDefinitionRequest request,
        string? createdBy,
        DateTimeOffset now)
    {
        return new WorkflowDefinition
        {
            Id = id,
            OrganizationId = organizationId,
            Name = request.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            Version = 1,
            Status = WorkflowDefinitionStatus.Draft,
            StartStepId = request.StartStepId,
            Steps = request.Steps ?? Array.Empty<WorkflowStepDefinition>(),
            CreatedAt = now,
            PublishedAt = null,
            ArchivedAt = null,
            CreatedBy = createdBy,
            Metadata = WorkflowDefinitionTriggers.With(request.Metadata, request.Trigger)
        };
    }

    public static void ApplyUpdate(WorkflowDefinition definition, UpdateWorkflowDefinitionRequest request)
    {
        definition.Name = request.Name.Trim();
        definition.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        definition.StartStepId = request.StartStepId;
        definition.Steps = request.Steps ?? Array.Empty<WorkflowStepDefinition>();
        definition.Metadata = WorkflowDefinitionTriggers.With(request.Metadata, request.Trigger);
    }

    public static WorkflowDefinitionResponse ToResponse(
        WorkflowDefinition definition,
        DateTimeOffset? updatedAt = null,
        string? updatedBy = null)
    {
        return new WorkflowDefinitionResponse
        {
            Id = definition.Id,
            OrganizationId = definition.OrganizationId,
            Name = definition.Name,
            Description = definition.Description,
            Version = definition.Version,
            Status = definition.Status.ToString(),
            StartStepId = definition.StartStepId,
            Trigger = EnsureTrigger(WorkflowDefinitionTriggers.Get(definition)),
            Steps = definition.Steps?.ToList() ?? [],
            Metadata = WorkflowDefinitionTriggers.WithoutTrigger(definition.Metadata),
            CreatedAt = definition.CreatedAt,
            CreatedBy = definition.CreatedBy,
            UpdatedAt = updatedAt,
            UpdatedBy = updatedBy,
            PublishedAt = definition.PublishedAt,
            ArchivedAt = definition.ArchivedAt
        };
    }

    public static WorkflowDefinitionListItemResponse ToListItem(WorkflowDefinitionRecord row)
    {
        return new WorkflowDefinitionListItemResponse
        {
            Id = row.Id,
            Name = row.Name,
            Description = row.Description,
            Version = row.Version,
            Status = row.Status,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt,
            PublishedAt = row.PublishedAt,
            ArchivedAt = row.ArchivedAt
        };
    }

    public static string SerializeDefinitionDocument(WorkflowDefinition definition) =>
        WorkflowJsonSerializer.Serialize(ToDocument(definition));

    public static string? SerializeMetadata(IReadOnlyDictionary<string, JsonElement>? metadata)
    {
        var persisted = WorkflowDefinitionTriggers.WithoutTrigger(metadata);
        if (persisted is null || persisted.Count == 0)
            return null;

        return WorkflowJsonSerializer.Serialize(persisted);
    }

    public static WorkflowDefinition FromRecord(WorkflowDefinitionRecord row)
    {
        var document = WorkflowJsonSerializer.Deserialize<WorkflowDefinitionDocument>(row.Definition)
            ?? throw new InvalidOperationException($"Definition document for '{row.Id}' is invalid.");

        IReadOnlyDictionary<string, JsonElement> metadata =
            new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(row.Metadata))
        {
            metadata = WorkflowJsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.Metadata)
                ?? metadata;
        }
        else if (document.Metadata is not null)
        {
            metadata = document.Metadata;
        }

        if (!Enum.TryParse<WorkflowDefinitionStatus>(row.Status, ignoreCase: true, out var status))
            status = WorkflowDefinitionStatus.Draft;

        return new WorkflowDefinition
        {
            Id = row.Id,
            OrganizationId = row.OrganizationId,
            Name = row.Name,
            Description = row.Description,
            Version = row.Version,
            Status = status,
            StartStepId = row.StartStepId,
            Steps = document.Steps ?? Array.Empty<WorkflowStepDefinition>(),
            CreatedAt = row.CreatedAt,
            PublishedAt = row.PublishedAt,
            ArchivedAt = row.ArchivedAt,
            CreatedBy = row.CreatedBy,
            Metadata = WorkflowDefinitionTriggers.With(metadata, document.Trigger)
        };
    }

    /// <summary>
    /// Legacy definitions without Trigger deserialize as manual start.
    /// </summary>
    public static WorkflowTrigger EnsureTrigger(WorkflowTrigger? trigger) =>
        trigger ?? new WorkflowTrigger { Type = "manual" };

    private static WorkflowDefinitionDocument ToDocument(WorkflowDefinition definition) =>
        new()
        {
            Id = definition.Id,
            OrganizationId = definition.OrganizationId,
            Name = definition.Name,
            Description = definition.Description,
            Version = definition.Version,
            Status = definition.Status.ToString(),
            StartStepId = definition.StartStepId,
            Trigger = EnsureTrigger(WorkflowDefinitionTriggers.Get(definition)),
            Steps = definition.Steps?.ToList() ?? [],
            CreatedAt = definition.CreatedAt,
            PublishedAt = definition.PublishedAt,
            ArchivedAt = definition.ArchivedAt,
            CreatedBy = definition.CreatedBy,
            Metadata = WorkflowDefinitionTriggers.WithoutTrigger(definition.Metadata)
        };
}
