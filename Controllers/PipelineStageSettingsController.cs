using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs;
using nexthire_api.Exceptions;
using nexthire_api.Security;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

[ApiController]
[Route("api/org/settings/pipeline-stages")]
[Authorize]
public class PipelineStageSettingsController : ControllerBase
{
    private readonly IPipelineStageSettingsService _stages;
    private readonly ILogger<PipelineStageSettingsController> _logger;

    public PipelineStageSettingsController(
        IPipelineStageSettingsService stages,
        ILogger<PipelineStageSettingsController> logger)
    {
        _stages = stages;
        _logger = logger;
    }

    /// <summary>
    /// Pipeline columns for the current organization. Seeds the default columns only when the org has none.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PipelineStageSettingsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<PipelineStageSettingsDto>>> List(CancellationToken cancellationToken)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var nexaUserId = ClaimUtils.RequireNexaUserId(User);
            var stages = await _stages.ListAsync(orgId, nexaUserId, cancellationToken);
            return Ok(stages);
        }
        catch (Exception ex)
        {
            return Map(ex, "Error listing pipeline stages");
        }
    }

    /// <summary>
    /// Add a pipeline column at the end of the sequence.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(IReadOnlyList<PipelineStageSettingsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<IReadOnlyList<PipelineStageSettingsDto>>> Create(
        [FromBody] CreatePipelineStageRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var orgId = ClaimUtils.RequireOrgId(User);
            var nexaUserId = ClaimUtils.RequireNexaUserId(User);
            var stages = await _stages.CreateAsync(orgId, nexaUserId, request.Name, cancellationToken);
            return Ok(stages);
        }
        catch (Exception ex)
        {
            return Map(ex, "Error creating pipeline stage");
        }
    }

    /// <summary>
    /// Rename a pipeline column. Candidates stay on the same column.
    /// </summary>
    [HttpPatch("{id:guid}")]
    [ProducesResponseType(typeof(IReadOnlyList<PipelineStageSettingsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<IReadOnlyList<PipelineStageSettingsDto>>> Rename(
        Guid id,
        [FromBody] RenamePipelineStageRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var orgId = ClaimUtils.RequireOrgId(User);
            var nexaUserId = ClaimUtils.RequireNexaUserId(User);
            var stages = await _stages.RenameAsync(orgId, nexaUserId, id, request.Name, cancellationToken);
            return Ok(stages);
        }
        catch (Exception ex)
        {
            return Map(ex, "Error renaming pipeline stage");
        }
    }

    /// <summary>
    /// Replace the column sequence. The list must include every column of the organization exactly once.
    /// </summary>
    [HttpPut("order")]
    [ProducesResponseType(typeof(IReadOnlyList<PipelineStageSettingsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<PipelineStageSettingsDto>>> Reorder(
        [FromBody] ReorderPipelineStagesRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var orgId = ClaimUtils.RequireOrgId(User);
            var nexaUserId = ClaimUtils.RequireNexaUserId(User);
            var stages = await _stages.ReorderAsync(orgId, nexaUserId, request.StageIds, cancellationToken);
            return Ok(stages);
        }
        catch (Exception ex)
        {
            return Map(ex, "Error reordering pipeline stages");
        }
    }

    /// <summary>
    /// Delete a column that has no candidates and no stage history. The organization must keep at least one column.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(typeof(IReadOnlyList<PipelineStageSettingsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<IReadOnlyList<PipelineStageSettingsDto>>> Delete(
        Guid id,
        CancellationToken cancellationToken)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var nexaUserId = ClaimUtils.RequireNexaUserId(User);
            var stages = await _stages.DeleteAsync(orgId, nexaUserId, id, cancellationToken);
            return Ok(stages);
        }
        catch (Exception ex)
        {
            return Map(ex, "Error deleting pipeline stage");
        }
    }

    private ActionResult Map(Exception ex, string logMessage)
    {
        switch (ex)
        {
            case PipelineStageException stageEx:
                return StatusCode(stageEx.StatusCode, new { message = stageEx.Message, code = stageEx.Code });
            case UnauthorizedAccessException unauthorized:
                var status = unauthorized.Message.Contains("owners and admins", StringComparison.OrdinalIgnoreCase)
                    ? StatusCodes.Status403Forbidden
                    : StatusCodes.Status401Unauthorized;
                return StatusCode(status, new { message = unauthorized.Message });
            default:
                _logger.LogError(ex, logMessage);
                return StatusCode(500, new { message = "An error occurred while updating pipeline columns." });
        }
    }
}
