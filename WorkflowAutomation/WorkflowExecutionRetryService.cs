using System.Text.Json;
using Nexa.WorkflowAutomation.Contracts.Persistence;
using Nexa.WorkflowAutomation.Engine;
using Nexa.WorkflowAutomation.Enums;
using Nexa.WorkflowAutomation.Exceptions;
using Nexa.WorkflowAutomation.Models.Executions;
using Nexa.WorkflowAutomation.Serialization;

namespace nexthire_api.WorkflowAutomation;

/// <summary>
/// Retries a Failed workflow execution from the failed step only.
/// Does not restart the workflow or re-run succeeded steps.
/// </summary>
/// <remarks>
/// <para>
/// Side-effect risk: retrying activities such as sendEmail, sendWhatsApp, documents.send,
/// documents.sendForSignature, meetings.schedule, ai.execute, or conversation.askQuestions
/// may repeat external calls. Prior StepExecution rows are retained; a new attempt is created
/// by the engine. No additional idempotency layer is added here.
/// </para>
/// <para>
/// MaxAttempts uses <c>WorkflowStepDefinition.MaxAttempts ?? 1</c> (package default).
/// When the failed step already reached that ceiling, Retry returns conflict.
/// Note: the engine may auto-retry while Attempt &lt; MaxAttempts before marking the workflow Failed;
/// after that exhaustion, host Retry is rejected by the same ceiling.
/// </para>
/// </remarks>
public interface IWorkflowExecutionRetryService
{
    /// <summary>
    /// Prepares Failed → Pending (transactional) then executes the current failed step via the engine.
    /// </summary>
    Task<WorkflowExecution> RetryAsync(
        Guid organizationId,
        Guid executionId,
        CancellationToken cancellationToken = default);
}

public sealed class WorkflowExecutionRetryService : IWorkflowExecutionRetryService
{
    private readonly IWorkflowExecutionRepository _executions;
    private readonly IWorkflowStepExecutionRepository _steps;
    private readonly IWorkflowDefinitionRepository _definitions;
    private readonly IWorkflowHistoryRepository _history;
    private readonly IWorkflowUnitOfWork _unitOfWork;
    private readonly IWorkflowEngine _engine;

    public WorkflowExecutionRetryService(
        IWorkflowExecutionRepository executions,
        IWorkflowStepExecutionRepository steps,
        IWorkflowDefinitionRepository definitions,
        IWorkflowHistoryRepository history,
        IWorkflowUnitOfWork unitOfWork,
        IWorkflowEngine engine)
    {
        _executions = executions;
        _steps = steps;
        _definitions = definitions;
        _history = history;
        _unitOfWork = unitOfWork;
        _engine = engine;
    }

    public async Task<WorkflowExecution> RetryAsync(
        Guid organizationId,
        Guid executionId,
        CancellationToken cancellationToken = default)
    {
        await _unitOfWork.ExecuteAsync(
            async ct =>
            {
                await PrepareRetryAsync(organizationId, executionId, ct);
            },
            cancellationToken);

        var execution = await _engine.ExecuteCurrentStepAsync(organizationId, executionId, cancellationToken);

        var guard = 0;
        while (execution.Status is WorkflowExecutionStatus.Pending or WorkflowExecutionStatus.Running)
        {
            if (++guard > 50)
            {
                throw new WorkflowAutomationException(
                    "Retry exceeded the maximum number of step iterations.");
            }

            execution = await _engine.ExecuteCurrentStepAsync(organizationId, executionId, cancellationToken);
        }

        return execution;
    }

    private async Task PrepareRetryAsync(
        Guid organizationId,
        Guid executionId,
        CancellationToken cancellationToken)
    {
        var execution = await _executions.GetByIdAsync(organizationId, executionId, cancellationToken);
        if (execution is null || execution.OrganizationId != organizationId)
            throw new WorkflowExecutionNotFoundException(organizationId, executionId);

        if (execution.Status != WorkflowExecutionStatus.Failed)
        {
            throw new WorkflowInvalidStateException(
                execution.Id,
                execution.Status,
                "retry (only Failed executions can be retried)");
        }

        if (execution.CurrentStepId is null)
        {
            throw new WorkflowInvalidStateException(
                execution.Id,
                execution.Status,
                "retry because CurrentStepId is missing");
        }

        var failedStep = await ResolveFailedStepAsync(organizationId, execution, cancellationToken);
        if (failedStep is null)
        {
            throw new WorkflowInvalidStateException(
                execution.Id,
                execution.Status,
                "retry because no consistent Failed step execution was found");
        }

        var definition = await _definitions.GetByIdAsync(
            organizationId,
            execution.WorkflowDefinitionId,
            cancellationToken);

        if (definition is null)
        {
            throw new WorkflowAutomationException(
                $"Workflow definition '{execution.WorkflowDefinitionId}' was not found.");
        }

        var stepDefinition = definition.FindStep(execution.CurrentStepId.Value)
            ?? throw new WorkflowAutomationException(
                $"Current step '{execution.CurrentStepId}' was not found in definition '{definition.Id}'.");

        if (failedStep.StepDefinitionId != stepDefinition.Id)
        {
            throw new WorkflowInvalidStateException(
                execution.Id,
                execution.Status,
                "retry because failed step does not match CurrentStepId");
        }

        // Package default: MaxAttempts ?? 1 (includes the first try).
        var maxAttempts = stepDefinition.MaxAttempts ?? 1;
        var previousAttempt = failedStep.Attempt;
        var newAttempt = previousAttempt + 1;
        if (newAttempt > maxAttempts)
        {
            throw new WorkflowInvalidStateException(
                execution.Id,
                execution.Status,
                $"retry because MaxAttempts ({maxAttempts}) has been reached");
        }

        var now = DateTimeOffset.UtcNow;
        var expectedRevision = execution.Revision;

        await _history.AddAsync(
            new WorkflowHistoryEntry
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = execution.Id,
                EventType = WorkflowHistoryEventType.WorkflowResumed,
                OccurredAt = now,
                Message = $"Retry requested for step '{stepDefinition.Name}' (attempt {previousAttempt} → {newAttempt}).",
                Data = WorkflowJsonSerializer.SerializeToElement(new
                {
                    executionId = execution.Id,
                    stepId = stepDefinition.Id,
                    previousAttempt,
                    newAttempt,
                    timestamp = now
                })
            },
            cancellationToken);

        // Failed → Pending; keep CurrentStepId + CurrentStepExecutionId so the engine increments Attempt.
        execution.Status = WorkflowExecutionStatus.Pending;
        execution.FailedAt = null;
        execution.FailureReason = null;
        execution.WaitingSince = null;
        execution.ResumeAt = null;

        try
        {
            await _executions.UpdateAsync(execution, expectedRevision, cancellationToken);
        }
        catch (WorkflowConcurrencyException)
        {
            throw new WorkflowInvalidStateException(
                execution.Id,
                WorkflowExecutionStatus.Failed,
                "retry because another concurrent retry won the race");
        }
    }

    private async Task<WorkflowStepExecution?> ResolveFailedStepAsync(
        Guid organizationId,
        WorkflowExecution execution,
        CancellationToken cancellationToken)
    {
        if (execution.CurrentStepExecutionId is Guid stepExecutionId)
        {
            var current = await _steps.GetByIdAsync(organizationId, stepExecutionId, cancellationToken);
            if (current is not null &&
                current.WorkflowExecutionId == execution.Id &&
                current.Status == WorkflowStepExecutionStatus.Failed &&
                execution.CurrentStepId is Guid currentStepId &&
                current.StepDefinitionId == currentStepId)
            {
                return current;
            }
        }

        return null;
    }
}
