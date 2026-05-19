using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Data.SqlClient;
using nexthire_api.DTOs;
using nexthire_api.Models.Notes;
using nexthire_api.Repositories;
using nexthire_api.Security;
using nexthire_api.Services;
using nexthire_api.Models.Tasks;

namespace nexthire_api.Controllers;

[ApiController]
[Route("api/applications")]
[Authorize]
public class ApplicationsController : ControllerBase
{
    private readonly IApplicationsService _service;
    private readonly IApplicationsRepository _repo;
    private readonly IPipelineRepository _pipelineRepo;
    private readonly ITasksRepository _tasksRepo;
    private readonly INotesRepository _notesRepo;
    private readonly ILogger<ApplicationsController> _logger;

    public ApplicationsController(
        IApplicationsService service,
        IApplicationsRepository repo,
        IPipelineRepository pipelineRepo,
        ITasksRepository tasksRepo,
        INotesRepository notesRepo,
        ILogger<ApplicationsController> logger)
    {
        _service = service;
        _repo = repo;
        _pipelineRepo = pipelineRepo;
        _tasksRepo = tasksRepo;
        _notesRepo = notesRepo;
        _logger = logger;
    }

    /// <summary>
    /// List applications (org-scoped).
    /// Filters by applied_at when present; otherwise created_at.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<ApplicationListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PagedResult<ApplicationListItemDto>>> GetApplications(
        [FromQuery] Guid? jobId,
        [FromQuery] Guid? candidateId,
        [FromQuery(Name = "stageId")] Guid? stageId,
        [FromQuery] string? status,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        try
        {
            if (page < 1) return BadRequest(new { message = "page must be >= 1" });
            if (pageSize < 10 || pageSize > 100) return BadRequest(new { message = "pageSize must be between 10 and 100" });

            var orgId = ClaimUtils.RequireOrgId(User);

            var result = await _service.GetPagedAsync(orgId, jobId, candidateId, stageId, status, from, to, search, page, pageSize);
            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing applications");
            return StatusCode(500, new { message = "An error occurred while retrieving applications" });
        }
    }

    /// <summary>
    /// Get application detail (org-scoped).
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ApplicationDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApplicationDetailDto>> GetApplication(Guid id)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);

            var detail = await _service.GetDetailAsync(orgId, id);
            if (detail == null)
                return NotFound(new { message = $"Application with ID {id} not found" });

            return Ok(detail);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting application {ApplicationId}", id);
            return StatusCode(500, new { message = "An error occurred while retrieving the application" });
        }
    }

    /// <summary>
    /// Get all tasks for a specific application (org-scoped).
    /// </summary>
    [HttpGet("{id}/tasks")]
    [ProducesResponseType(typeof(IEnumerable<TaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<TaskDto>>> GetApplicationTasks(Guid id)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);

            // Validate application exists in org
            var app = await _repo.GetByIdAsync(orgId, id);
            if (app == null)
                return NotFound(new { message = $"Application with ID {id} not found" });

            var tasks = await _tasksRepo.GetByApplicationIdAsync(orgId, id);
            return Ok(tasks);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting tasks for application {ApplicationId}", id);
            return StatusCode(500, new { message = "An error occurred while retrieving application tasks" });
        }
    }

    /// <summary>
    /// Get all notes for a specific application (org-scoped).
    /// </summary>
    [HttpGet("{id}/notes")]
    [ProducesResponseType(typeof(IEnumerable<NoteDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<NoteDto>>> GetApplicationNotes(Guid id, [FromQuery] int limit = 100)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);

            if (limit < 1 || limit > 200)
                return BadRequest(new { message = "limit must be between 1 and 200" });

            // Validate application exists in org
            var app = await _repo.GetByIdAsync(orgId, id);
            if (app == null)
                return NotFound(new { message = $"Application with ID {id} not found" });

            var notes = await _notesRepo.GetByApplicationIdAsync(orgId, id, limit);
            return Ok(notes);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting notes for application {ApplicationId}", id);
            return StatusCode(500, new { message = "An error occurred while retrieving application notes" });
        }
    }

    /// <summary>
    /// Create a new note for a specific application (org-scoped).
    /// </summary>
    [HttpPost("{id}/notes")]
    [ProducesResponseType(typeof(NoteDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NoteDto>> CreateApplicationNote(Guid id, [FromBody] UpdateNoteRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Body))
                return BadRequest(new { message = "body is required" });

            var orgId = ClaimUtils.RequireOrgId(User);

            // Validate application exists in org
            var app = await _repo.GetByIdAsync(orgId, id);
            if (app == null)
                return NotFound(new { message = $"Application with ID {id} not found" });

            // Notes author is required in this DB (author_user_id NOT NULL), so always resolve nh_users.id from token.
            var nexaUserId = ClaimUtils.RequireNexaUserId(User);
            var email = ClaimUtils.GetEmail(User);
            var displayName = ClaimUtils.GetDisplayName(User);
            var createdByUserId = await _repo.GetOrCreateNhUserIdAsync(orgId, nexaUserId, email, displayName);

            var noteId = Guid.NewGuid();
            var created = await _notesRepo.CreateAsync(orgId, noteId, id, request.Body.Trim(), createdByUserId);
            return CreatedAtAction("GetNote", "Notes", new { id = created.Id }, created);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating note for application {ApplicationId}", id);
            return StatusCode(500, new { message = "An error occurred while creating the note" });
        }
    }

    /// <summary>
    /// Get application stage history with human-readable joins (stages + moved-by user).
    /// </summary>
    [HttpGet("{id}/stage-history")]
    [ProducesResponseType(typeof(IEnumerable<ApplicationStageHistoryItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<ApplicationStageHistoryItemDto>>> GetStageHistory(Guid id, [FromQuery] int limit = 50)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);

            if (limit < 1 || limit > 200)
                return BadRequest(new { message = "limit must be between 1 and 200" });

            // Ensure application belongs to org
            var app = await _repo.GetByIdAsync(orgId, id);
            if (app == null)
                return NotFound(new { message = $"Application with ID {id} not found" });

            var history = await _repo.GetStageHistoryAsync(orgId, id, limit);
            return Ok(history);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting stage history for application {ApplicationId}", id);
            return StatusCode(500, new { message = "An error occurred while retrieving stage history" });
        }
    }

    /// <summary>
    /// Create a new application and insert initial stage history row.
    /// From the candidates UI you can also use POST /api/candidates/{candidateId}/applications with the same job/stage options (candidate id in the path only).
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApplicationListItemDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<object>> CreateApplication([FromBody] CreateApplicationRequest request)
    {
        try
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var orgId = ClaimUtils.RequireOrgId(User);
            var nexaUserId = ClaimUtils.RequireNexaUserId(User);
            var email = ClaimUtils.GetEmail(User);
            var displayName = ClaimUtils.GetDisplayName(User);

            var createdCard = await _service.CreateKanbanApplicationAsync(
                orgId,
                nexaUserId,
                email,
                displayName,
                request.JobId,
                request.CandidateId,
                request.CurrentStageId,
                request.Status);

            return CreatedAtAction(nameof(GetApplicationKanbanDetail), new { id = createdCard.Id }, createdCard);
        }
        catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627)
        {
            // Unique constraint violation (e.g., UX_applications_job_candidate)
            _logger.LogInformation(
                ex,
                "Duplicate application prevented by unique index. OrgId: {OrgId}, JobId: {JobId}, CandidateId: {CandidateId}",
                ClaimUtils.RequireOrgId(User),
                request.JobId,
                request.CandidateId);

            return Conflict(new
            {
                message = "This candidate already has an application for the selected job."
            });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(500, new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating application");
            return StatusCode(500, new { message = "An error occurred while creating the application" });
        }
    }

    /// <summary>
    /// Kanban board for a job: { stages: [...], columns: { [stageId]: ApplicationCardDto[] } }
    /// </summary>
    [HttpGet("kanban")]
    [ProducesResponseType(typeof(KanbanBoardDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<KanbanBoardDto>> GetKanban([FromQuery] Guid jobId)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);

            if (!await _pipelineRepo.JobExistsInOrgAsync(orgId, jobId))
                return BadRequest(new { message = "jobId not found in this organization" });

            var stages = await _pipelineRepo.EnsureDefaultStagesAsync(orgId);
            var cards = await _repo.GetKanbanCardsAsync(orgId, jobId);

            var columns = new Dictionary<Guid, List<ApplicationCardDto>>();
            foreach (var stage in stages)
                columns[stage.Id] = new List<ApplicationCardDto>();

            foreach (var card in cards)
            {
                if (!columns.TryGetValue(card.CurrentStageId, out var list))
                {
                    columns[card.CurrentStageId] = new List<ApplicationCardDto> { card };
                }
                else
                {
                    list.Add(card);
                }
            }

            return Ok(new KanbanBoardDto
            {
                Stages = stages,
                Columns = columns
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting kanban board");
            return StatusCode(500, new { message = "An error occurred while retrieving kanban board" });
        }
    }

    /// <summary>
    /// Move application to another stage (Kanban drag/drop).
    /// </summary>
    [HttpPatch("{id}/move")]
    [ProducesResponseType(typeof(ApplicationCardDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApplicationCardDto>> MoveApplication(Guid id, [FromBody] MoveApplicationRequest request)
    {
        try
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var orgId = ClaimUtils.RequireOrgId(User);
            var nexaUserId = ClaimUtils.RequireNexaUserId(User);
            var email = ClaimUtils.GetEmail(User);
            var displayName = ClaimUtils.GetDisplayName(User);

            var nhUserId = await _repo.GetOrCreateNhUserIdAsync(orgId, nexaUserId, email, displayName);

            if (!await _pipelineRepo.StageExistsInOrgAsync(orgId, request.ToStageId))
                return BadRequest(new { message = "toStageId not found in this organization" });

            var updated = await _repo.MoveKanbanAsync(orgId, id, request.ToStageId, nhUserId);
            if (updated == null)
                return NotFound(new { message = $"Application with ID {id} not found" });

            return Ok(updated);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error moving application {ApplicationId}", id);
            return StatusCode(500, new { message = "An error occurred while moving application" });
        }
    }

    /// <summary>
    /// Minimal application detail for Kanban UI (application + candidate + job + current stage).
    /// </summary>
    [HttpGet("{id}/kanban-detail")]
    [ProducesResponseType(typeof(ApplicationListItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApplicationListItemDto>> GetApplicationKanbanDetail(Guid id)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var app = await _repo.GetByIdAsync(orgId, id);
            if (app == null)
                return NotFound(new { message = $"Application with ID {id} not found" });

            return Ok(app);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Patch application (status/currentStageId/appliedAt).
    /// If stage changes, inserts stage history row.
    /// </summary>
    [HttpPatch("{id}")]
    [ProducesResponseType(typeof(ApplicationListItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApplicationListItemDto>> PatchApplication(Guid id, [FromBody] UpdateApplicationRequestDto request)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var nexaUserId = ClaimUtils.RequireNexaUserId(User);
            var email = ClaimUtils.GetEmail(User);
            var displayName = ClaimUtils.GetDisplayName(User);

            var nhUserId = await _repo.GetOrCreateNhUserIdAsync(orgId, nexaUserId, email, displayName);

            var updated = await _service.PatchAsync(orgId, nhUserId, id, request);
            if (updated == null)
                return NotFound(new { message = $"Application with ID {id} not found" });

            return Ok(updated);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error patching application {ApplicationId}", id);
            return StatusCode(500, new { message = "An error occurred while updating the application" });
        }
    }

    /// <summary>
    /// Update application status (idempotent).
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(ApplicationListItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApplicationListItemDto>> UpdateApplicationStatus(Guid id, [FromBody] UpdateApplicationStatusRequestDto request)
    {
        try
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var orgId = ClaimUtils.RequireOrgId(User);
            var nexaUserId = ClaimUtils.RequireNexaUserId(User);
            var email = ClaimUtils.GetEmail(User);
            var displayName = ClaimUtils.GetDisplayName(User);

            var nhUserId = await _repo.GetOrCreateNhUserIdAsync(orgId, nexaUserId, email, displayName);

            var updated = await _service.PatchAsync(orgId, nhUserId, id, new UpdateApplicationRequestDto
            {
                Status = request.Status
            });

            if (updated == null)
                return NotFound(new { message = $"Application with ID {id} not found" });

            return Ok(updated);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating application status {ApplicationId}", id);
            return StatusCode(500, new { message = "An error occurred while updating the application status" });
        }
    }

    /// <summary>
    /// Move application stage (Kanban drag/drop).
    /// </summary>
    [HttpPost("{id}/move-stage")]
    [ProducesResponseType(typeof(ApplicationDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApplicationDetailDto>> MoveStage(Guid id, [FromBody] MoveStageRequestDto request)
    {
        try
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var orgId = ClaimUtils.RequireOrgId(User);
            var nexaUserId = ClaimUtils.RequireNexaUserId(User);
            var email = ClaimUtils.GetEmail(User);
            var displayName = ClaimUtils.GetDisplayName(User);

            var nhUserId = await _repo.GetOrCreateNhUserIdAsync(orgId, nexaUserId, email, displayName);

            var result = await _service.MoveStageAsync(orgId, nhUserId, id, request);
            if (result == null)
                return NotFound(new { message = $"Application with ID {id} not found" });

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error moving stage for application {ApplicationId}", id);
            return StatusCode(500, new { message = "An error occurred while moving application stage" });
        }
    }

    /// <summary>
    /// Archive application (soft delete via status='archived').
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteApplication(Guid id)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);

            var archived = await _service.DeleteAsync(orgId, id);
            if (archived == null)
                return NotFound(new { message = $"Application with ID {id} not found" });

            return NoContent();
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting application {ApplicationId}", id);
            return StatusCode(500, new { message = "An error occurred while deleting the application" });
        }
    }
}

