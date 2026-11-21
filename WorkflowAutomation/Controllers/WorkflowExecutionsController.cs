using nexthire_api.Security;
using nexthire_api.WorkflowAutomation.DTOs;
using nexthire_api.WorkflowAutomation.Mapping;
using nexthire_api.WorkflowAutomation.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexa.WorkflowAutomation.Engine;
using Nexa.WorkflowAutomation.Enums;
using Nexa.WorkflowAutomation.Exceptions;
using Nexa.WorkflowAutomation.Models.Events;
using Nexa.WorkflowAutomation.Models.Executions;
using Nexa.WorkflowAutomation.Serialization;

namespace nexthire_api.WorkflowAutomation.Controllers;

[ApiController]
[Route("api/workflow-executions")]
[Authorize]
public sealed class WorkflowExecutionsController : ControllerBase
{
    private readonly SqlWorkflowExecutionRepository _executions;
    private readonly SqlWorkflowDefinitionRepository _definitions;
    private readonly SqlWorkflowHistoryRepository _history;
    private readonly SqlWorkflowStepExecutionRepository _steps;
    private readonly SqlWorkflowEventReceiptRepository _eventReceipts;
    private readonly IWorkflowExecutionRetryService _retry;
    private readonly IWorkflowEngine _engine;

    public WorkflowExecutionsController(
        SqlWorkflowExecutionRepository executions,
        SqlWorkflowDefinitionRepository definitions,
        SqlWorkflowHistoryRepository history,
        SqlWorkflowStepExecutionRepository steps,
        SqlWorkflowEventReceiptRepository eventReceipts,
        IWorkflowExecutionRetryService retry,
        IWorkflowEngine engine)
    {
        _executions = executions;
        _definitions = definitions;
        _history = history;
        _steps = steps;
        _eventReceipts = eventReceipts;
        _retry = retry;
        _engine = engine;
    }

    /// <summary>Lists workflow executions for the current organization.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<WorkflowExecutionListItemResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<WorkflowExecutionListItemResponse>>> List(
        CancellationToken cancellationToken)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var items = await _executions.ListAsync(orgId, cancellationToken);
            return Ok(items);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
    }

    /// <summary>Gets a workflow execution by id.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(WorkflowExecutionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WorkflowExecutionResponse>> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var record = await _executions.GetRecordByIdAsync(orgId, id, cancellationToken);
            if (record is null)
                return NotFound();

            var execution = WorkflowExecutionMapper.FromRecord(record);
            return Ok(WorkflowExecutionMapper.ToResponse(execution, record.UpdatedAt));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
    }

    /// <summary>Returns history entries for an execution (<c>nh_wf_history</c>), ordered by occurredAt ASC.</summary>
    [HttpGet("{id:guid}/history")]
    [ProducesResponseType(typeof(IReadOnlyList<WorkflowExecutionHistoryItemResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<WorkflowExecutionHistoryItemResponse>>> GetHistory(
        Guid id,
        CancellationToken cancellationToken)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            if (!await ExecutionExistsAsync(orgId, id, cancellationToken))
                return NotFound();

            var items = await _history.ListByExecutionIdAsync(orgId, id, cancellationToken);
            return Ok(items);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
    }

    /// <summary>Returns step executions for an execution (<c>nh_wf_step_executions</c>), ordered by createdAt ASC.</summary>
    [HttpGet("{id:guid}/steps")]
    [ProducesResponseType(typeof(IReadOnlyList<WorkflowExecutionStepItemResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<WorkflowExecutionStepItemResponse>>> GetSteps(
        Guid id,
        CancellationToken cancellationToken)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            if (!await ExecutionExistsAsync(orgId, id, cancellationToken))
                return NotFound();

            var steps = await _steps.ListByExecutionIdAsync(orgId, id, cancellationToken);
            return Ok(steps.Select(WorkflowExecutionMapper.ToStepItem).ToList());
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Returns persisted event receipts for an execution (<c>nh_wf_event_receipts</c>).
    /// Does not invent payloads — only eventId, eventType, correlationId, processedAt.
    /// </summary>
    [HttpGet("{id:guid}/events")]
    [ProducesResponseType(typeof(IReadOnlyList<WorkflowExecutionEventReceiptResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<WorkflowExecutionEventReceiptResponse>>> GetEvents(
        Guid id,
        CancellationToken cancellationToken)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            if (!await ExecutionExistsAsync(orgId, id, cancellationToken))
                return NotFound();

            var items = await _eventReceipts.ListByExecutionIdAsync(orgId, id, cancellationToken);
            return Ok(items);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Retries a Failed execution from the failed step only (new StepExecution attempt).
    /// Does not restart the workflow or re-run succeeded steps.
    /// Side-effect risk: external activities (email, WhatsApp, documents, etc.) may run again.
    /// </summary>
    [HttpPost("{id:guid}/retry")]
    [ProducesResponseType(typeof(WorkflowExecutionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WorkflowExecutionResponse>> Retry(
        Guid id,
        CancellationToken cancellationToken)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var execution = await _retry.RetryAsync(orgId, id, cancellationToken);
            return Ok(WorkflowExecutionMapper.ToResponse(execution, DateTimeOffset.UtcNow));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (WorkflowExecutionNotFoundException)
        {
            return NotFound();
        }
        catch (WorkflowInvalidStateException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (WorkflowConcurrencyException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (WorkflowAutomationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Creates a Pending workflow execution without running any step.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(WorkflowExecutionResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WorkflowExecutionResponse>> Create(
        [FromBody] CreateWorkflowExecutionRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);

            if (request.WorkflowDefinitionId == Guid.Empty)
                return BadRequest(new { message = "workflowDefinitionId is required." });

            if (string.IsNullOrWhiteSpace(request.EntityType))
                return BadRequest(new { message = "entityType is required." });

            if (string.IsNullOrWhiteSpace(request.EntityId))
                return BadRequest(new { message = "entityId is required." });

            var definition = await _definitions.GetByIdAsync(
                orgId,
                request.WorkflowDefinitionId,
                cancellationToken);

            if (definition is null)
                return NotFound(new { message = "Workflow definition was not found." });

            if (definition.Status != WorkflowDefinitionStatus.Published)
            {
                return BadRequest(new
                {
                    message = $"Workflow definition must be Published. Current status is '{definition.Status}'."
                });
            }

            var now = DateTimeOffset.UtcNow;
            var execution = WorkflowExecutionMapper.CreatePending(
                orgId,
                Guid.NewGuid(),
                definition,
                request.EntityType,
                request.EntityId,
                now,
                request.InitialVariables);

            await _executions.AddAsync(execution, cancellationToken);

            return CreatedAtAction(
                nameof(GetById),
                new { id = execution.Id },
                WorkflowExecutionMapper.ToResponse(execution));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Executes the current step via the Workflow Engine.
    /// Continues while status is Pending or Running so sendEmail → waitForEvent
    /// stops at Waiting, and sendEmail → end completes in one call.
    /// </summary>
    [HttpPost("{id:guid}/execute")]
    [ProducesResponseType(typeof(WorkflowExecutionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WorkflowExecutionResponse>> Execute(
        Guid id,
        CancellationToken cancellationToken)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var existing = await _executions.GetByIdAsync(orgId, id, cancellationToken);
            if (existing is null)
                return NotFound();

            var execution = await RunPendingStepsAsync(orgId, id, existing, cancellationToken);
            return Ok(WorkflowExecutionMapper.ToResponse(execution, DateTimeOffset.UtcNow));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (WorkflowExecutionNotFoundException)
        {
            return NotFound();
        }
        catch (WorkflowInvalidStateException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (WorkflowAutomationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Delivers a normalized event to a Waiting execution and continues the workflow.
    /// Uses <see cref="IWorkflowEngine.ResumeFromEventAsync"/> (package API).
    /// </summary>
    [HttpPost("{id:guid}/events")]
    [ProducesResponseType(typeof(WorkflowExecutionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WorkflowExecutionResponse>> ProcessEvent(
        Guid id,
        [FromBody] ProcessWorkflowEventRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);

            if (string.IsNullOrWhiteSpace(request.EventType))
                return BadRequest(new { message = "eventType is required." });

            var existing = await _executions.GetByIdAsync(orgId, id, cancellationToken);
            if (existing is null)
                return NotFound();

            if (existing.Status != WorkflowExecutionStatus.Waiting)
            {
                return Conflict(new
                {
                    message = $"Only Waiting executions can receive events. Current status is '{existing.Status}'."
                });
            }

            var workflowEvent = new WorkflowEvent
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                WorkflowExecutionId = id,
                EventType = request.EventType.Trim(),
                CorrelationId = string.IsNullOrWhiteSpace(request.CorrelationId)
                    ? null
                    : request.CorrelationId.Trim(),
                EntityType = existing.EntityType,
                EntityId = existing.EntityId,
                OccurredAt = DateTimeOffset.UtcNow,
                Payload = request.Payload ?? WorkflowJsonSerializer.SerializeToElement(new { })
            };

            var execution = await _engine.ResumeFromEventAsync(
                new ResumeWorkflowFromEventRequest
                {
                    ExecutionId = id,
                    Event = workflowEvent
                },
                cancellationToken);

            // After resume the next step is typically Pending (e.g. flow.end).
            execution = await RunPendingStepsAsync(orgId, id, execution, cancellationToken);

            return Ok(WorkflowExecutionMapper.ToResponse(execution, DateTimeOffset.UtcNow));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (WorkflowExecutionNotFoundException)
        {
            return NotFound();
        }
        catch (WorkflowInvalidStateException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (WorkflowAutomationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Cancels a workflow execution without running any step.</summary>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(typeof(WorkflowExecutionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WorkflowExecutionResponse>> Cancel(
        Guid id,
        CancellationToken cancellationToken)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var record = await _executions.GetRecordByIdAsync(orgId, id, cancellationToken);
            if (record is null)
                return NotFound();

            var execution = WorkflowExecutionMapper.FromRecord(record);
            if (execution.Status is WorkflowExecutionStatus.Cancelled
                or WorkflowExecutionStatus.Completed
                or WorkflowExecutionStatus.Failed)
            {
                return Conflict(new
                {
                    message = $"Execution cannot be cancelled from status '{execution.Status}'."
                });
            }

            var expectedRevision = execution.Revision;
            var now = DateTimeOffset.UtcNow;
            execution.Status = WorkflowExecutionStatus.Cancelled;
            execution.CancelledAt = now;

            await _executions.UpdateAsync(execution, expectedRevision, cancellationToken);
            return Ok(WorkflowExecutionMapper.ToResponse(execution, now));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (WorkflowConcurrencyException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Resumes a Waiting execution after <c>resumeAt</c> has elapsed (e.g. flow.delay).
    /// Uses <see cref="IWorkflowEngine.ResumeFromTimeoutAsync"/> then continues pending steps.
    /// Returns 409 when the execution is not Waiting or <c>resumeAt</c> is still in the future.
    /// </summary>
    [HttpPost("{id:guid}/resume")]
    [ProducesResponseType(typeof(WorkflowExecutionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WorkflowExecutionResponse>> Resume(
        Guid id,
        CancellationToken cancellationToken)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var existing = await _executions.GetByIdAsync(orgId, id, cancellationToken);
            if (existing is null)
                return NotFound();

            if (existing.Status != WorkflowExecutionStatus.Waiting)
            {
                return Conflict(new
                {
                    message = $"Only Waiting executions can be resumed. Current status is '{existing.Status}'."
                });
            }

            var now = DateTimeOffset.UtcNow;
            if (existing.ResumeAt is null || now < existing.ResumeAt)
            {
                return Conflict(new
                {
                    message = existing.ResumeAt is null
                        ? "Execution has no resumeAt and cannot be resumed via this endpoint."
                        : $"resumeAt '{existing.ResumeAt:O}' has not been reached yet."
                });
            }

            var execution = await _engine.ResumeFromTimeoutAsync(orgId, id, now, cancellationToken);
            if (execution.Status is WorkflowExecutionStatus.Pending or WorkflowExecutionStatus.Running)
                execution = await RunPendingStepsAsync(orgId, id, execution, cancellationToken);

            return Ok(WorkflowExecutionMapper.ToResponse(execution, DateTimeOffset.UtcNow));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (WorkflowExecutionNotFoundException)
        {
            return NotFound();
        }
        catch (WorkflowInvalidStateException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (WorkflowConcurrencyException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (WorkflowAutomationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    private async Task<WorkflowExecution> RunPendingStepsAsync(
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
                    "Execution exceeded the maximum number of step iterations.");
            }

            execution = await _engine.ExecuteCurrentStepAsync(organizationId, executionId, cancellationToken);
        }

        return execution;
    }

    private async Task<bool> ExecutionExistsAsync(
        Guid organizationId,
        Guid executionId,
        CancellationToken cancellationToken) =>
        await _executions.ExistsAsync(organizationId, executionId, cancellationToken);
}