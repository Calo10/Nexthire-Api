using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs;
using nexthire_api.DTOs.CoreHr;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

/// <summary>
/// Core HR employee onboardings. Note the plural route: <c>api/Onboarding</c> / <c>api/onboarding/provision</c>
/// belong to organization provisioning and are unrelated to this controller.
/// </summary>
[ApiController]
[Route("api/onboardings")]
[Authorize]
public class OnboardingsController : CoreHrControllerBase
{
    private readonly IOnboardingService _onboardings;

    public OnboardingsController(IOnboardingService onboardings, ILogger<OnboardingsController> logger)
        : base(logger)
    {
        _onboardings = onboardings;
    }

    // -------------------------------------------------------------------------
    // Read
    // -------------------------------------------------------------------------

    /// <summary>Current user's onboarding capabilities (derived from role codes).</summary>
    [HttpGet("capabilities")]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    public Task<IActionResult> GetCapabilities(CancellationToken ct) =>
        RunAsync(async a => Ok(await _onboardings.GetCapabilitiesAsync(a, ct)), nameof(GetCapabilities));

    /// <summary>Paged, filterable, sortable onboarding list.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<OnboardingListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> List([FromQuery] OnboardingListQuery query, CancellationToken ct) =>
        RunAsync(async a => Ok(await _onboardings.ListAsync(a, query, ct)), nameof(List));

    /// <summary>Kanban pipeline: NotStarted, InProgress, InReview and Completed columns (Cancelled excluded).</summary>
    [HttpGet("pipeline")]
    [ProducesResponseType(typeof(OnboardingPipelineDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Pipeline([FromQuery] OnboardingPipelineQuery query, CancellationToken ct) =>
        RunAsync(async a => Ok(await _onboardings.GetPipelineAsync(a, query, ct)), nameof(Pipeline));

    /// <summary>Summary metrics with previous-period comparison.</summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(OnboardingSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Summary([FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct) =>
        RunAsync(async a => Ok(await _onboardings.GetSummaryAsync(a, from, to, ct)), nameof(Summary));

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(OnboardingDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var detail = await _onboardings.GetAsync(a, id, ct);
            return detail == null ? NotFoundResult("Onboarding") : Ok(detail);
        }, nameof(Get));

    [HttpGet("{id:guid}/activity")]
    [ProducesResponseType(typeof(IReadOnlyList<OnboardingActivityDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Activity(Guid id, [FromQuery] int take = 50, CancellationToken ct = default) =>
        RunAsync(async a => Ok(await _onboardings.GetActivityAsync(a, id, take, ct)), nameof(Activity));

    // -------------------------------------------------------------------------
    // Create / update
    // -------------------------------------------------------------------------

    [HttpPost]
    [ProducesResponseType(typeof(OnboardingDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Create([FromBody] CreateOnboardingRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var created = await _onboardings.CreateAsync(a, request, ct);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }, nameof(Create));

    /// <summary>
    /// Idempotent "start onboarding" for an employee: returns the active onboarding when one exists, otherwise creates one
    /// with the best matching template. The frontend calls this right after <c>POST api/employees/convert-from-candidate</c>
    /// (convert itself does not start onboarding, which keeps the two services free of a circular dependency).
    /// </summary>
    [HttpPost("from-employee/{employeeId:guid}")]
    [ProducesResponseType(typeof(OnboardingDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> StartForEmployee(Guid employeeId, [FromBody] StartOnboardingForEmployeeRequest? request, CancellationToken ct) =>
        RunAsync(async a => Ok(await _onboardings.StartOnboardingForEmployeeAsync(a, employeeId, request, ct)), nameof(StartForEmployee));

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(OnboardingDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Update(Guid id, [FromBody] UpdateOnboardingRequest request, CancellationToken ct) =>
        RunAsync(async a => Detail(await _onboardings.UpdateAsync(a, id, request, ct)), nameof(Update));

    // -------------------------------------------------------------------------
    // Status
    // -------------------------------------------------------------------------

    /// <summary>NotStarted -> InProgress.</summary>
    [HttpPost("{id:guid}/start")]
    [ProducesResponseType(typeof(OnboardingDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Start(Guid id, CancellationToken ct) =>
        RunAsync(async a => Detail(await _onboardings.StartAsync(a, id, ct)), nameof(Start));

    /// <summary>InProgress -> InReview. Fails with 409 until every required task is complete (and approved where needed).</summary>
    [HttpPost("{id:guid}/submit-review")]
    [ProducesResponseType(typeof(OnboardingDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> SubmitForReview(Guid id, CancellationToken ct) =>
        RunAsync(async a => Detail(await _onboardings.SubmitForReviewAsync(a, id, ct)), nameof(SubmitForReview));

    /// <summary>InReview -> InProgress.</summary>
    [HttpPost("{id:guid}/return-to-progress")]
    [ProducesResponseType(typeof(OnboardingDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> ReturnToProgress(Guid id, CancellationToken ct) =>
        RunAsync(async a => Detail(await _onboardings.ReturnToProgressAsync(a, id, ct)), nameof(ReturnToProgress));

    /// <summary>-> Completed. Fails with 409 until every required task is complete (and approved where needed).</summary>
    [HttpPost("{id:guid}/complete")]
    [ProducesResponseType(typeof(OnboardingDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Complete(Guid id, [FromBody] CompleteOnboardingRequest? request, CancellationToken ct) =>
        RunAsync(async a => Detail(await _onboardings.CompleteAsync(a, id, request, ct)), nameof(Complete));

    /// <summary>Any active status -> Cancelled (reason required).</summary>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(typeof(OnboardingDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Cancel(Guid id, [FromBody] CancelOnboardingRequest request, CancellationToken ct) =>
        RunAsync(async a => Detail(await _onboardings.CancelAsync(a, id, request, ct)), nameof(Cancel));

    /// <summary>Generic status change for Kanban drag-and-drop; dragging to Completed applies the same validation as Complete.</summary>
    [HttpPost("{id:guid}/status")]
    [ProducesResponseType(typeof(OnboardingDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> ChangeStatus(Guid id, [FromBody] ChangeOnboardingStatusRequest request, CancellationToken ct) =>
        RunAsync(async a => Detail(await _onboardings.ChangeStatusAsync(a, id, request, ct)), nameof(ChangeStatus));

    // -------------------------------------------------------------------------
    // Tasks
    // -------------------------------------------------------------------------

    [HttpPost("{id:guid}/tasks")]
    [ProducesResponseType(typeof(OnboardingTaskDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> AddTask(Guid id, [FromBody] CreateOnboardingTaskRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var task = await _onboardings.AddTaskAsync(a, id, request, ct);
            return Created($"/api/onboardings/{id}/tasks/{task.Id}", task);
        }, nameof(AddTask));

    [HttpPut("{id:guid}/tasks/{taskId:guid}")]
    [ProducesResponseType(typeof(OnboardingTaskDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> UpdateTask(Guid id, Guid taskId, [FromBody] UpdateOnboardingTaskRequest request, CancellationToken ct) =>
        RunAsync(async a => TaskResult(await _onboardings.UpdateTaskAsync(a, id, taskId, request, ct)), nameof(UpdateTask));

    /// <summary>Cancels a task (it no longer counts toward progress).</summary>
    [HttpDelete("{id:guid}/tasks/{taskId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> CancelTask(Guid id, Guid taskId, CancellationToken ct) =>
        RunAsync(async a => await _onboardings.CancelTaskAsync(a, id, taskId, ct) ? NoContent() : NotFoundResult("Task"), nameof(CancelTask));

    [HttpPost("{id:guid}/tasks/{taskId:guid}/complete")]
    [ProducesResponseType(typeof(OnboardingTaskDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> CompleteTask(Guid id, Guid taskId, [FromBody] CompleteOnboardingTaskRequest? request, CancellationToken ct) =>
        RunAsync(async a => TaskResult(await _onboardings.CompleteTaskAsync(a, id, taskId, request, ct)), nameof(CompleteTask));

    [HttpPost("{id:guid}/tasks/{taskId:guid}/reopen")]
    [ProducesResponseType(typeof(OnboardingTaskDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> ReopenTask(Guid id, Guid taskId, CancellationToken ct) =>
        RunAsync(async a => TaskResult(await _onboardings.ReopenTaskAsync(a, id, taskId, ct)), nameof(ReopenTask));

    [HttpPost("{id:guid}/tasks/{taskId:guid}/approve")]
    [ProducesResponseType(typeof(OnboardingTaskDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> ApproveTask(Guid id, Guid taskId, [FromBody] ApproveOnboardingTaskRequest? request, CancellationToken ct) =>
        RunAsync(async a => TaskResult(await _onboardings.ApproveTaskAsync(a, id, taskId, request, ct)), nameof(ApproveTask));

    [HttpPost("{id:guid}/tasks/{taskId:guid}/reject")]
    [ProducesResponseType(typeof(OnboardingTaskDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> RejectTask(Guid id, Guid taskId, [FromBody] RejectOnboardingTaskRequest request, CancellationToken ct) =>
        RunAsync(async a => TaskResult(await _onboardings.RejectTaskAsync(a, id, taskId, request, ct)), nameof(RejectTask));

    // -------------------------------------------------------------------------
    // Documents
    // -------------------------------------------------------------------------

    [HttpGet("{id:guid}/documents")]
    [ProducesResponseType(typeof(IReadOnlyList<OnboardingDocumentDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> ListDocuments(Guid id, CancellationToken ct) =>
        RunAsync(async a => Ok(await _onboardings.ListDocumentsAsync(a, id, ct)), nameof(ListDocuments));

    [HttpPut("{id:guid}/documents/{documentId:guid}")]
    [ProducesResponseType(typeof(OnboardingDocumentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> UpdateDocument(Guid id, Guid documentId, [FromBody] UpdateOnboardingDocumentRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var doc = await _onboardings.UpdateDocumentAsync(a, id, documentId, request, ct);
            return doc == null ? NotFoundResult("Document") : Ok(doc);
        }, nameof(UpdateDocument));

    // -------------------------------------------------------------------------
    // Notification settings / log
    // -------------------------------------------------------------------------

    [HttpGet("notification-settings")]
    [ProducesResponseType(typeof(IReadOnlyList<OnboardingNotificationSettingDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> GetNotificationSettings(CancellationToken ct) =>
        RunAsync(async a => Ok(await _onboardings.GetNotificationSettingsAsync(a, ct)), nameof(GetNotificationSettings));

    [HttpPut("notification-settings")]
    [ProducesResponseType(typeof(IReadOnlyList<OnboardingNotificationSettingDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> UpdateNotificationSettings([FromBody] UpdateOnboardingNotificationSettingsRequest request, CancellationToken ct) =>
        RunAsync(async a => Ok(await _onboardings.UpdateNotificationSettingsAsync(a, request, ct)), nameof(UpdateNotificationSettings));

    /// <summary>Recent notification attempts (Queued / Sent / Failed / Skipped).</summary>
    [HttpGet("notifications")]
    [ProducesResponseType(typeof(IReadOnlyList<OnboardingNotificationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> ListNotifications([FromQuery] Guid? onboardingId, [FromQuery] int take = 50, CancellationToken ct = default) =>
        RunAsync(async a => Ok(await _onboardings.ListNotificationsAsync(a, onboardingId, take, ct)), nameof(ListNotifications));

    // -------------------------------------------------------------------------

    private IActionResult Detail(OnboardingDetailDto? detail) => detail == null ? NotFoundResult("Onboarding") : Ok(detail);

    private IActionResult TaskResult(OnboardingTaskDto? task) => task == null ? NotFoundResult("Task") : Ok(task);
}
