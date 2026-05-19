using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.Models.Tasks;
using nexthire_api.Security;
using nexthire_api.Repositories;

namespace nexthire_api.Controllers;

[ApiController]
[Route("api/tasks")]
[Authorize]
public class TasksController : ControllerBase
{
    private readonly ITasksRepository _tasksRepository;
    private readonly ILogger<TasksController> _logger;

    public TasksController(ITasksRepository tasksRepository, ILogger<TasksController> logger)
    {
        _tasksRepository = tasksRepository;
        _logger = logger;
    }

    /// <summary>
    /// Get tasks for the current org (org-scoped via applications.org_id)
    /// </summary>
    /// <remarks>
    /// Filters:
    /// - from/to (optional): filters by task due_at (tasks without due_at are excluded when from/to is provided)
    /// - search (optional): matches task title (contains, case-insensitive)
    /// - jobId/candidateId/applicationId (optional): filters via applications join
    /// - page/pageSize: paging for large lists
    ///
    /// Backwards-compat (deprecated):
    /// - q maps to search
    /// - limit maps to pageSize
    /// - offset maps to page (best-effort)
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<TaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IEnumerable<TaskDto>>> GetTasks(
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? sort = "created_at",
        [FromQuery] string? dir = "desc",
        // Task-specific optional filters (kept, but base paging/search matches /api/candidates)
        [FromQuery] string? status = null,
        [FromQuery] Guid? jobId = null,
        [FromQuery] Guid? candidateId = null,
        [FromQuery] Guid? applicationId = null,
        // Optional date range for tasks (filters by due_at)
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery(Name = "q")] string? q = null,
        [FromQuery] int? limit = null,
        [FromQuery] int? offset = null)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);

            // Back-compat: q -> search
            if (string.IsNullOrWhiteSpace(search) && !string.IsNullOrWhiteSpace(q))
                search = q;

            // Back-compat: limit/offset -> page/pageSize (best-effort)
            if (limit.HasValue && limit.Value > 0)
                pageSize = limit.Value;

            // Align with /api/candidates validation
            if (pageSize < 1 || pageSize > 200)
                return BadRequest(new { message = "pageSize must be between 1 and 200" });

            if (page < 1)
                return BadRequest(new { message = "page must be >= 1" });

            if (offset.HasValue && offset.Value >= 0)
            {
                // Only derive page if caller didn't explicitly set page (i.e., stayed at default 1).
                if (page == 1)
                    page = (offset.Value / pageSize) + 1;
            }

            // Normalize status input to avoid surprising mismatches.
            status = NormalizeStatus(status);

            var items = await _tasksRepository.GetListAsync(
                orgId,
                from,
                to,
                status,
                jobId,
                candidateId,
                applicationId,
                search,
                page,
                pageSize,
                sort ?? "created_at",
                dir ?? "desc");

            return Ok(items);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting tasks");
            return StatusCode(500, new { message = "An error occurred while retrieving tasks" });
        }
    }

    /// <summary>
    /// Create a new task (org-scoped via applications.org_id)
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(TaskDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<TaskDto>> CreateTask([FromBody] CreateTaskRequest request)
    {
        try
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var orgId = ClaimUtils.RequireOrgId(User);

            var title = request.Title?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(title) || title.Length > 400)
                return BadRequest(new { message = "title is required and must be <= 400 characters" });

            var status = NormalizeStatus(request.Status) ?? "todo";
            if (!IsAllowedStatus(status))
                return BadRequest(new { message = "Invalid status. Allowed: todo, in_progress, blocked, done" });

            // Validate application belongs to org.
            var appOrgId = await _tasksRepository.GetApplicationOrgIdAsync(request.ApplicationId);
            if (!appOrgId.HasValue)
                return BadRequest(new { message = "applicationId not found" });
            if (appOrgId.Value != orgId)
                return Forbid();

            // Optional: validate assigned user belongs to org.
            if (request.AssignedToUserId.HasValue)
            {
                var userOrgId = await _tasksRepository.GetUserOrgIdAsync(request.AssignedToUserId.Value);
                if (!userOrgId.HasValue)
                    return BadRequest(new { message = "assignedToUserId not found" });
                if (userOrgId.Value != orgId)
                    return Forbid();
            }

            var id = Guid.NewGuid();
            var created = await _tasksRepository.CreateAsync(orgId, id, request, status);
            if (created == null)
            {
                _logger.LogWarning("Task create failed. OrgId: {OrgId}, ApplicationId: {ApplicationId}", orgId, request.ApplicationId);
                return StatusCode(500, new { message = "Unable to create task" });
            }

            return CreatedAtAction(nameof(GetTask), new { id = created.Id }, created);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating task");
            return StatusCode(500, new { message = "An error occurred while creating the task" });
        }
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(TaskDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TaskDto>> GetTask(Guid id)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);

            var task = await _tasksRepository.GetByIdAsync(orgId, id);
            if (task == null)
            {
                var actualOrgId = await _tasksRepository.GetTaskOrgIdAsync(id);
                if (actualOrgId.HasValue && actualOrgId.Value != orgId)
                    return Forbid();

                return NotFound(new { message = $"Task with ID {id} not found" });
            }

            return Ok(task);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting task {TaskId}", id);
            return StatusCode(500, new { message = "An error occurred while retrieving the task" });
        }
    }

    /// <summary>
    /// Update only task status (Kanban)
    /// </summary>
    [HttpPatch("{id}/status")]
    [ProducesResponseType(typeof(TaskDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TaskDto>> UpdateTaskStatus(Guid id, [FromBody] UpdateTaskStatusRequest request)
    {
        try
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var orgId = ClaimUtils.RequireOrgId(User);

            var status = NormalizeStatus(request.Status);
            if (string.IsNullOrWhiteSpace(status) || !IsAllowedStatus(status))
                return BadRequest(new { message = "Invalid status. Allowed: todo, in_progress, blocked, done" });

            var updated = await _tasksRepository.UpdateStatusAsync(orgId, id, status);
            if (!updated)
            {
                var actualOrgId = await _tasksRepository.GetTaskOrgIdAsync(id);
                if (actualOrgId.HasValue && actualOrgId.Value != orgId)
                    return Forbid();
                return NotFound(new { message = $"Task with ID {id} not found" });
            }

            var dto = await _tasksRepository.GetByIdAsync(orgId, id);
            return dto == null ? NotFound(new { message = $"Task with ID {id} not found" }) : Ok(dto);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating task status {TaskId}", id);
            return StatusCode(500, new { message = "An error occurred while updating the task status" });
        }
    }

    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteTask(Guid id)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);

            var deleted = await _tasksRepository.DeleteAsync(orgId, id);
            if (!deleted)
            {
                var actualOrgId = await _tasksRepository.GetTaskOrgIdAsync(id);
                if (actualOrgId.HasValue && actualOrgId.Value != orgId)
                    return Forbid();
                return NotFound(new { message = $"Task with ID {id} not found" });
            }

            return NoContent();
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting task {TaskId}", id);
            return StatusCode(500, new { message = "An error occurred while deleting the task" });
        }
    }

    private static string? NormalizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;

        var s = status.Trim();
        var lower = s.ToLowerInvariant();

        // Accept old and display variants, but normalize to API/DB snake_case values.
        return lower switch
        {
            "todo" => "todo",
            "to do" => "todo",
            "to-do" => "todo",
            "in_progress" => "in_progress",
            "in progress" => "in_progress",
            "in-progress" => "in_progress",
            "blocked" => "blocked",
            "block" => "blocked",
            "done" => "done",
            _ => lower
        };
    }

    private static bool IsAllowedStatus(string status)
    {
        return status is "todo" or "in_progress" or "blocked" or "done";
    }
}

