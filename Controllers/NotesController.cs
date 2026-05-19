using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.Models.Notes;
using nexthire_api.Repositories;
using nexthire_api.Security;

namespace nexthire_api.Controllers;

[ApiController]
[Route("api/notes")]
[Authorize]
public class NotesController : ControllerBase
{
    private readonly INotesRepository _notesRepo;
    private readonly IApplicationsRepository _applicationsRepo;
    private readonly ILogger<NotesController> _logger;

    public NotesController(INotesRepository notesRepo, IApplicationsRepository applicationsRepo, ILogger<NotesController> logger)
    {
        _notesRepo = notesRepo;
        _applicationsRepo = applicationsRepo;
        _logger = logger;
    }

    /// <summary>
    /// Get a note by id (org-scoped).
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(NoteDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NoteDto>> GetNote(Guid id)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);

            var note = await _notesRepo.GetByIdAsync(orgId, id);
            if (note is null)
                return NotFound(new { message = $"Note with ID {id} not found" });

            return Ok(note);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting note {NoteId}", id);
            return StatusCode(500, new { message = "An error occurred while retrieving the note" });
        }
    }

    /// <summary>
    /// Create a new note for an application (org-scoped).
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(NoteDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NoteDto>> CreateNote([FromBody] CreateNoteRequest request)
    {
        try
        {
            if (request.ApplicationId == Guid.Empty)
                return BadRequest(new { message = "applicationId is required" });
            if (string.IsNullOrWhiteSpace(request.Body))
                return BadRequest(new { message = "body is required" });

            var orgId = ClaimUtils.RequireOrgId(User);

            // Validate application exists in org
            var app = await _applicationsRepo.GetByIdAsync(orgId, request.ApplicationId);
            if (app is null)
                return NotFound(new { message = $"Application with ID {request.ApplicationId} not found" });

            // Notes author is required in this DB (author_user_id NOT NULL), so always resolve nh_users.id from token.
            var nexaUserId = ClaimUtils.RequireNexaUserId(User);
            var email = ClaimUtils.GetEmail(User);
            var displayName = ClaimUtils.GetDisplayName(User);
            var createdByUserId = await _applicationsRepo.GetOrCreateNhUserIdAsync(orgId, nexaUserId, email, displayName);

            var id = Guid.NewGuid();
            var created = await _notesRepo.CreateAsync(orgId, id, request.ApplicationId, request.Body.Trim(), createdByUserId);
            return CreatedAtAction(nameof(GetNote), new { id = created.Id }, created);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating note. ApplicationId: {ApplicationId}", request.ApplicationId);
            return StatusCode(500, new { message = "An error occurred while creating the note" });
        }
    }

    /// <summary>
    /// Update a note body (org-scoped).
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(NoteDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NoteDto>> UpdateNote(Guid id, [FromBody] UpdateNoteRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Body))
                return BadRequest(new { message = "body is required" });

            var orgId = ClaimUtils.RequireOrgId(User);

            var updated = await _notesRepo.UpdateAsync(orgId, id, request.Body.Trim());
            if (!updated)
                return NotFound(new { message = $"Note with ID {id} not found" });

            var note = await _notesRepo.GetByIdAsync(orgId, id);
            if (note is null)
                return NotFound(new { message = $"Note with ID {id} not found" });

            return Ok(note);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating note {NoteId}", id);
            return StatusCode(500, new { message = "An error occurred while updating the note" });
        }
    }

    /// <summary>
    /// Delete a note (org-scoped).
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteNote(Guid id)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);

            var deleted = await _notesRepo.DeleteAsync(orgId, id);
            if (!deleted)
                return NotFound(new { message = $"Note with ID {id} not found" });

            return NoContent();
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting note {NoteId}", id);
            return StatusCode(500, new { message = "An error occurred while deleting the note" });
        }
    }
}

