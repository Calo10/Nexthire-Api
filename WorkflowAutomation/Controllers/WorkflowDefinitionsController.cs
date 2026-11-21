using nexthire_api.Security;
using nexthire_api.WorkflowAutomation.DTOs;
using nexthire_api.WorkflowAutomation.Mapping;
using nexthire_api.WorkflowAutomation.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexa.WorkflowAutomation.Contracts.Activities;
using Nexa.WorkflowAutomation.Enums;
using Nexa.WorkflowAutomation.Exceptions;

namespace nexthire_api.WorkflowAutomation.Controllers;

[ApiController]
[Route("api/workflow-definitions")]
[Authorize]
public sealed class WorkflowDefinitionsController : ControllerBase
{
    private readonly SqlWorkflowDefinitionRepository _repository;
    private readonly IWorkflowDefinitionValidator _validator;

    public WorkflowDefinitionsController(
        SqlWorkflowDefinitionRepository repository,
        IWorkflowDefinitionValidator validator)
    {
        _repository = repository;
        _validator = validator;
    }

    /// <summary>Lists workflow definitions for the current organization.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<WorkflowDefinitionListItemResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<WorkflowDefinitionListItemResponse>>> List(
        CancellationToken cancellationToken)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var items = await _repository.ListAsync(orgId, cancellationToken);
            return Ok(items);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
    }

    /// <summary>Gets a workflow definition by id.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(WorkflowDefinitionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WorkflowDefinitionResponse>> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var record = await _repository.GetRecordByIdAsync(orgId, id, cancellationToken);
            if (record is null)
                return NotFound();

            var definition = WorkflowDefinitionMapper.FromRecord(record);
            return Ok(WorkflowDefinitionMapper.ToResponse(definition, record.UpdatedAt, record.UpdatedBy));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
    }

    /// <summary>Creates a draft workflow definition.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(WorkflowDefinitionResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<WorkflowDefinitionResponse>> Create(
        [FromBody] CreateWorkflowDefinitionRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            string? createdBy = null;
            try { createdBy = ClaimUtils.RequireNexaUserId(User).ToString(); }
            catch (UnauthorizedAccessException) { /* optional */ }

            var now = DateTimeOffset.UtcNow;
            var definition = WorkflowDefinitionMapper.ToDomain(orgId, Guid.NewGuid(), request, createdBy, now);

            EnsureValid(definition);

            await _repository.AddAsync(definition, cancellationToken);
            return CreatedAtAction(
                nameof(GetById),
                new { id = definition.Id },
                WorkflowDefinitionMapper.ToResponse(definition));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (WorkflowDefinitionValidationException ex)
        {
            return BadRequest(new
            {
                message = ex.Message,
                errors = ex.Result.ErrorFindings.Select(e => new
                {
                    e.Code,
                    e.Message,
                    e.StepId
                })
            });
        }
    }

    /// <summary>Updates a draft workflow definition.</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(WorkflowDefinitionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WorkflowDefinitionResponse>> Update(
        Guid id,
        [FromBody] UpdateWorkflowDefinitionRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            string? updatedBy = null;
            try { updatedBy = ClaimUtils.RequireNexaUserId(User).ToString(); }
            catch (UnauthorizedAccessException) { /* optional */ }

            var record = await _repository.GetRecordByIdAsync(orgId, id, cancellationToken);
            if (record is null)
                return NotFound();

            var definition = WorkflowDefinitionMapper.FromRecord(record);
            if (definition.Status != WorkflowDefinitionStatus.Draft)
            {
                return Conflict(new
                {
                    message = $"Only Draft definitions can be updated. Current status is '{definition.Status}'."
                });
            }

            WorkflowDefinitionMapper.ApplyUpdate(definition, request);
            EnsureValid(definition);

            await _repository.UpdateAsync(definition, updatedBy, cancellationToken);
            return Ok(WorkflowDefinitionMapper.ToResponse(definition, DateTimeOffset.UtcNow, updatedBy));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (WorkflowDefinitionValidationException ex)
        {
            return BadRequest(new
            {
                message = ex.Message,
                errors = ex.Result.ErrorFindings.Select(e => new
                {
                    e.Code,
                    e.Message,
                    e.StepId
                })
            });
        }
    }

    /// <summary>Soft-deletes a workflow definition.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var deleted = await _repository.SoftDeleteAsync(orgId, id, cancellationToken);
            return deleted ? NoContent() : NotFound();
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
    }

    /// <summary>Publishes a draft workflow definition.</summary>
    [HttpPost("{id:guid}/publish")]
    [ProducesResponseType(typeof(WorkflowDefinitionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WorkflowDefinitionResponse>> Publish(
        Guid id,
        CancellationToken cancellationToken)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            string? updatedBy = null;
            try { updatedBy = ClaimUtils.RequireNexaUserId(User).ToString(); }
            catch (UnauthorizedAccessException) { /* optional */ }

            var record = await _repository.GetRecordByIdAsync(orgId, id, cancellationToken);
            if (record is null)
                return NotFound();

            var definition = WorkflowDefinitionMapper.FromRecord(record);
            if (definition.Status != WorkflowDefinitionStatus.Draft)
            {
                return Conflict(new
                {
                    message = $"Only Draft definitions can be published. Current status is '{definition.Status}'."
                });
            }

            definition.Status = WorkflowDefinitionStatus.Published;
            definition.PublishedAt = DateTimeOffset.UtcNow;
            EnsureValid(definition);

            await _repository.UpdateAsync(definition, updatedBy, cancellationToken);
            return Ok(WorkflowDefinitionMapper.ToResponse(definition, DateTimeOffset.UtcNow, updatedBy));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (WorkflowDefinitionValidationException ex)
        {
            return BadRequest(new
            {
                message = ex.Message,
                errors = ex.Result.ErrorFindings.Select(e => new
                {
                    e.Code,
                    e.Message,
                    e.StepId
                })
            });
        }
    }

    /// <summary>Archives a published or draft workflow definition.</summary>
    [HttpPost("{id:guid}/archive")]
    [ProducesResponseType(typeof(WorkflowDefinitionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WorkflowDefinitionResponse>> Archive(
        Guid id,
        CancellationToken cancellationToken)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            string? updatedBy = null;
            try { updatedBy = ClaimUtils.RequireNexaUserId(User).ToString(); }
            catch (UnauthorizedAccessException) { /* optional */ }

            var record = await _repository.GetRecordByIdAsync(orgId, id, cancellationToken);
            if (record is null)
                return NotFound();

            var definition = WorkflowDefinitionMapper.FromRecord(record);
            if (definition.Status == WorkflowDefinitionStatus.Archived)
            {
                return Conflict(new { message = "Definition is already Archived." });
            }

            definition.Status = WorkflowDefinitionStatus.Archived;
            definition.ArchivedAt = DateTimeOffset.UtcNow;

            await _repository.UpdateAsync(definition, updatedBy, cancellationToken);
            return Ok(WorkflowDefinitionMapper.ToResponse(definition, DateTimeOffset.UtcNow, updatedBy));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
    }

    private void EnsureValid(Nexa.WorkflowAutomation.Models.Definitions.WorkflowDefinition definition)
    {
        var result = _validator.Validate(definition);
        if (!result.IsValid)
            throw new WorkflowDefinitionValidationException(result);
    }
}
