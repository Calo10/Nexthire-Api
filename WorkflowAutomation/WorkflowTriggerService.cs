using System.Text.Json;
using nexthire_api.WorkflowAutomation.Mapping;
using Microsoft.Extensions.Logging;
using Nexa.WorkflowAutomation.Contracts.Persistence;
using Nexa.WorkflowAutomation.Engine;
using Nexa.WorkflowAutomation.Enums;
using Nexa.WorkflowAutomation.Exceptions;
using Nexa.WorkflowAutomation.Models.Definitions;
using Nexa.WorkflowAutomation.Models.Executions;

namespace nexthire_api.WorkflowAutomation;

/// <summary>
/// Dispatches NextHire business events to Published workflow definitions by trigger type.
/// </summary>
public interface IWorkflowTriggerService
{
    /// <summary>
    /// Finds Published definitions for <paramref name="triggerType"/> in the organization,
    /// creates one execution per match (idempotent), and runs each via the engine.
    /// Failures in individual workflows are logged and do not propagate.
    /// </summary>
    Task DispatchAsync(
        Guid organizationId,
        string triggerType,
        string entityType,
        string entityId,
        IReadOnlyDictionary<string, JsonElement>? variables = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Lookup of Published, non-deleted definitions matching a trigger type (SQL JSON filter).
/// </summary>
public interface IWorkflowTriggerDefinitionLookup
{
    Task<IReadOnlyList<WorkflowDefinition>> FindPublishedByTriggerTypeAsync(
        Guid organizationId,
        string triggerType,
        CancellationToken cancellationToken = default);
}

public sealed class WorkflowTriggerService : IWorkflowTriggerService
{
    private readonly IWorkflowTriggerDefinitionLookup _definitions;
    private readonly IWorkflowExecutionRepository _executions;
    private readonly IWorkflowEngine _engine;
    private readonly ILogger<WorkflowTriggerService> _logger;

    public WorkflowTriggerService(
        IWorkflowTriggerDefinitionLookup definitions,
        IWorkflowExecutionRepository executions,
        IWorkflowEngine engine,
        ILogger<WorkflowTriggerService> logger)
    {
        _definitions = definitions;
        _executions = executions;
        _engine = engine;
        _logger = logger;
    }

    public async Task DispatchAsync(
        Guid organizationId,
        string triggerType,
        string entityType,
        string entityId,
        IReadOnlyDictionary<string, JsonElement>? variables = null,
        CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty)
            throw new ArgumentException("OrganizationId is required.", nameof(organizationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerType);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);

        // manual triggers never auto-dispatch
        if (string.Equals(triggerType.Trim(), "manual", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "Skipping workflow trigger dispatch for manual trigger. Org={OrganizationId}",
                organizationId);
            return;
        }

        IReadOnlyList<WorkflowDefinition> definitions;
        try
        {
            definitions = await _definitions.FindPublishedByTriggerTypeAsync(
                organizationId,
                triggerType.Trim(),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to load workflow definitions for trigger {TriggerType}. Org={OrganizationId}",
                triggerType,
                organizationId);
            return;
        }

        if (definitions.Count == 0)
        {
            _logger.LogDebug(
                "No Published workflows for trigger {TriggerType}. Org={OrganizationId}",
                triggerType,
                organizationId);
            return;
        }

        foreach (var definition in definitions)
        {
            try
            {
                await StartAndRunAsync(
                    organizationId,
                    triggerType.Trim(),
                    entityType.Trim(),
                    entityId.Trim(),
                    definition,
                    variables,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Workflow trigger failed for definition {DefinitionId} ({DefinitionName}). Trigger={TriggerType}, Entity={EntityType}/{EntityId}, Org={OrganizationId}",
                    definition.Id,
                    definition.Name,
                    triggerType,
                    entityType,
                    entityId,
                    organizationId);
            }
        }
    }

    private async Task StartAndRunAsync(
        Guid organizationId,
        string triggerType,
        string entityType,
        string entityId,
        WorkflowDefinition definition,
        IReadOnlyDictionary<string, JsonElement>? variables,
        CancellationToken cancellationToken)
    {
        if (definition.Status != WorkflowDefinitionStatus.Published)
            return;

        if (string.Equals(WorkflowDefinitionTriggers.Get(definition).Type, "manual", StringComparison.OrdinalIgnoreCase))
            return;

        var executionId = IsOncePerEntity(triggerType)
            ? BuildIdempotentExecutionId(
                organizationId,
                triggerType,
                entityType,
                entityId,
                definition.Id)
            : Guid.NewGuid();

        if (IsOncePerEntity(triggerType))
        {
            var existing = await _executions.GetByIdAsync(organizationId, executionId, cancellationToken);
            if (existing is not null)
            {
                _logger.LogInformation(
                    "Skipping duplicate trigger execution. ExecutionId={ExecutionId}, DefinitionId={DefinitionId}, Trigger={TriggerType}, Entity={EntityType}/{EntityId}",
                    executionId,
                    definition.Id,
                    triggerType,
                    entityType,
                    entityId);
                return;
            }
        }

        var now = DateTimeOffset.UtcNow;
        var execution = WorkflowExecutionMapper.CreatePending(
            organizationId,
            executionId,
            definition,
            entityType,
            entityId,
            now,
            variables);

        try
        {
            await _executions.AddAsync(execution, cancellationToken);
        }
        catch (Exception ex) when (IsDuplicateExecutionConflict(ex))
        {
            _logger.LogInformation(
                ex,
                "Duplicate trigger execution insert ignored. ExecutionId={ExecutionId}, DefinitionId={DefinitionId}",
                executionId,
                definition.Id);
            return;
        }

        _logger.LogInformation(
            "Created workflow execution from trigger. ExecutionId={ExecutionId}, DefinitionId={DefinitionId}, Trigger={TriggerType}, Entity={EntityType}/{EntityId}",
            execution.Id,
            definition.Id,
            triggerType,
            entityType,
            entityId);

        execution = await RunUntilIdleAsync(organizationId, execution.Id, execution, cancellationToken);

        _logger.LogInformation(
            "Trigger execution finished. ExecutionId={ExecutionId}, Status={Status}",
            execution.Id,
            execution.Status);
    }

    private async Task<WorkflowExecution> RunUntilIdleAsync(
        Guid organizationId,
        Guid executionId,
        WorkflowExecution execution,
        CancellationToken cancellationToken)
    {
        var guard = 0;
        while (execution.Status is WorkflowExecutionStatus.Pending or WorkflowExecutionStatus.Running)
        {
            if (++guard > 50)
            {
                throw new WorkflowAutomationException(
                    "Trigger execution exceeded the maximum number of step iterations.");
            }

            execution = await _engine.ExecuteCurrentStepAsync(organizationId, executionId, cancellationToken);
        }

        return execution;
    }

    /// <summary>
    /// Deterministic execution id: org + trigger + entity + definition.
    /// Same event cannot insert twice (PK / GetById guard). No new table required.
    /// </summary>
    /// <summary>
    /// <c>lead.created</c> is once per lead+definition. Other triggers (updates, inbound messages)
    /// create a new execution each time.
    /// </summary>
    private static bool IsOncePerEntity(string triggerType)
    {
        var type = triggerType.Trim();
        return string.Equals(type, CandidateWorkflowVariables.CandidateCreatedTrigger, StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, CandidateWorkflowVariables.ApplicationCreatedTrigger, StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, CandidateWorkflowVariables.SourcingCompletedTrigger, StringComparison.OrdinalIgnoreCase);
    }

    public static Guid BuildIdempotentExecutionId(
        Guid organizationId,
        string triggerType,
        string entityType,
        string entityId,
        Guid workflowDefinitionId) =>
        WorkflowEventIdFactory.FromExternalKey(
            "workflow.trigger.start",
            $"{organizationId:D}|{triggerType.Trim().ToLowerInvariant()}|{entityType.Trim()}|{entityId.Trim()}|{workflowDefinitionId:D}");

    private static bool IsDuplicateExecutionConflict(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            var message = current.Message ?? string.Empty;
            if (message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("2627") ||
                message.Contains("2601"))
            {
                return true;
            }
        }

        return false;
    }
}
