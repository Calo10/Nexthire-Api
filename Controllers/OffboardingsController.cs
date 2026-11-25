using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs;
using nexthire_api.DTOs.CoreHr;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

/// <summary>
/// Core HR employee offboardings. Confidential fields (separation reason, confidential notes, exit interview responses and HR notes)
/// are only returned to callers with <c>Offboarding.ViewConfidentialInformation</c>.
/// </summary>
[ApiController]
[Route("api/offboardings")]
[Authorize]
public class OffboardingsController : CoreHrControllerBase
{
    private readonly IOffboardingService _offboardings;

    public OffboardingsController(IOffboardingService offboardings, ILogger<OffboardingsController> logger)
        : base(logger)
    {
        _offboardings = offboardings;
    }

    // -------------------------------------------------------------------------
    // Read
    // -------------------------------------------------------------------------

    /// <summary>Current user's offboarding capabilities (derived from role codes).</summary>
    [HttpGet("capabilities")]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    public Task<IActionResult> GetCapabilities(CancellationToken ct) =>
        RunAsync(async a => Ok(await _offboardings.GetCapabilitiesAsync(a, ct)), nameof(GetCapabilities));

    /// <summary>Paged, filterable, sortable offboarding list.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<OffboardingListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> List([FromQuery] OffboardingListQuery query, CancellationToken ct) =>
        RunAsync(async a => Ok(await _offboardings.ListAsync(a, query, ct)), nameof(List));

    /// <summary>Kanban pipeline: Scheduled, InProgress, PendingClearance and Completed columns (Cancelled excluded).</summary>
    [HttpGet("pipeline")]
    [ProducesResponseType(typeof(OffboardingPipelineDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Pipeline([FromQuery] OffboardingPipelineQuery query, CancellationToken ct) =>
        RunAsync(async a => Ok(await _offboardings.GetPipelineAsync(a, query, ct)), nameof(Pipeline));

    /// <summary>Summary metrics with previous-period comparison.</summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(OffboardingSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Summary([FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct) =>
        RunAsync(async a => Ok(await _offboardings.GetSummaryAsync(a, from, to, ct)), nameof(Summary));

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(OffboardingDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        RunAsync(async a => Detail(await _offboardings.GetAsync(a, id, ct)), nameof(Get));

    [HttpGet("{id:guid}/activity")]
    [ProducesResponseType(typeof(IReadOnlyList<OffboardingActivityDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Activity(Guid id, [FromQuery] int take = 50, CancellationToken ct = default) =>
        RunAsync(async a => Ok(await _offboardings.GetActivityAsync(a, id, take, ct)), nameof(Activity));

    // -------------------------------------------------------------------------
    // Create / update
    // -------------------------------------------------------------------------

    [HttpPost]
    [ProducesResponseType(typeof(OffboardingDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Create([FromBody] CreateOffboardingRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var created = await _offboardings.CreateAsync(a, request, ct);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }, nameof(Create));

    /// <summary>
    /// Idempotent "start offboarding" for an employee: returns the active offboarding when one exists, otherwise creates one with the
    /// best matching template. A <c>separationType</c> is required when a new offboarding has to be created.
    /// </summary>
    [HttpPost("from-employee/{employeeId:guid}")]
    [ProducesResponseType(typeof(OffboardingDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> StartForEmployee(Guid employeeId, [FromBody] StartOffboardingForEmployeeRequest? request, CancellationToken ct) =>
        RunAsync(async a => Ok(await _offboardings.StartOffboardingForEmployeeAsync(a, employeeId, request, ct)), nameof(StartForEmployee));

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(OffboardingDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Update(Guid id, [FromBody] UpdateOffboardingRequest request, CancellationToken ct) =>
        RunAsync(async a => Detail(await _offboardings.UpdateAsync(a, id, request, ct)), nameof(Update));

    // -------------------------------------------------------------------------
    // Status
    // -------------------------------------------------------------------------

    /// <summary>Scheduled -> InProgress.</summary>
    [HttpPost("{id:guid}/start")]
    [ProducesResponseType(typeof(OffboardingDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Start(Guid id, CancellationToken ct) =>
        RunAsync(async a => Detail(await _offboardings.StartAsync(a, id, ct)), nameof(Start));

    /// <summary>InProgress -> PendingClearance. Fails with 409 until every required task is complete (and approved where needed).</summary>
    [HttpPost("{id:guid}/submit-clearance")]
    [ProducesResponseType(typeof(OffboardingDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> SubmitClearance(Guid id, CancellationToken ct) =>
        RunAsync(async a => Detail(await _offboardings.SubmitClearanceAsync(a, id, ct)), nameof(SubmitClearance));

    /// <summary>PendingClearance -> InProgress.</summary>
    [HttpPost("{id:guid}/return-to-progress")]
    [ProducesResponseType(typeof(OffboardingDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> ReturnToProgress(Guid id, CancellationToken ct) =>
        RunAsync(async a => Detail(await _offboardings.ReturnToProgressAsync(a, id, ct)), nameof(ReturnToProgress));

    /// <summary>
    /// -> Completed. Fails with 409 until required tasks are complete and approved, required assets are returned or waived,
    /// system access is revoked or verified, and required settlement items are completed or waived.
    /// </summary>
    [HttpPost("{id:guid}/complete")]
    [ProducesResponseType(typeof(OffboardingDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Complete(Guid id, [FromBody] CompleteOffboardingRequest? request, CancellationToken ct) =>
        RunAsync(async a => Detail(await _offboardings.CompleteAsync(a, id, request, ct)), nameof(Complete));

    /// <summary>Any active status -> Cancelled (reason required). Does not restore the employee's employment status.</summary>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(typeof(OffboardingDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Cancel(Guid id, [FromBody] CancelOffboardingRequest request, CancellationToken ct) =>
        RunAsync(async a => Detail(await _offboardings.CancelAsync(a, id, request, ct)), nameof(Cancel));

    /// <summary>Generic status change for Kanban drag-and-drop; dragging to Completed applies the same validation as Complete.</summary>
    [HttpPost("{id:guid}/status")]
    [ProducesResponseType(typeof(OffboardingDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> ChangeStatus(Guid id, [FromBody] ChangeOffboardingStatusRequest request, CancellationToken ct) =>
        RunAsync(async a => Detail(await _offboardings.ChangeStatusAsync(a, id, request, ct)), nameof(ChangeStatus));

    // -------------------------------------------------------------------------
    // Tasks
    // -------------------------------------------------------------------------

    [HttpPost("{id:guid}/tasks")]
    [ProducesResponseType(typeof(OffboardingTaskDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> AddTask(Guid id, [FromBody] CreateOffboardingTaskRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var task = await _offboardings.AddTaskAsync(a, id, request, ct);
            return Created($"/api/offboardings/{id}/tasks/{task.Id}", task);
        }, nameof(AddTask));

    [HttpPut("{id:guid}/tasks/{taskId:guid}")]
    [ProducesResponseType(typeof(OffboardingTaskDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> UpdateTask(Guid id, Guid taskId, [FromBody] UpdateOffboardingTaskRequest request, CancellationToken ct) =>
        RunAsync(async a => TaskResult(await _offboardings.UpdateTaskAsync(a, id, taskId, request, ct)), nameof(UpdateTask));

    /// <summary>Cancels a task (it no longer counts toward progress).</summary>
    [HttpDelete("{id:guid}/tasks/{taskId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> CancelTask(Guid id, Guid taskId, CancellationToken ct) =>
        RunAsync(async a => await _offboardings.CancelTaskAsync(a, id, taskId, ct) ? NoContent() : NotFoundResult("Task"), nameof(CancelTask));

    [HttpPost("{id:guid}/tasks/{taskId:guid}/complete")]
    [ProducesResponseType(typeof(OffboardingTaskDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> CompleteTask(Guid id, Guid taskId, [FromBody] CompleteOffboardingTaskRequest? request, CancellationToken ct) =>
        RunAsync(async a => TaskResult(await _offboardings.CompleteTaskAsync(a, id, taskId, request, ct)), nameof(CompleteTask));

    [HttpPost("{id:guid}/tasks/{taskId:guid}/reopen")]
    [ProducesResponseType(typeof(OffboardingTaskDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> ReopenTask(Guid id, Guid taskId, CancellationToken ct) =>
        RunAsync(async a => TaskResult(await _offboardings.ReopenTaskAsync(a, id, taskId, ct)), nameof(ReopenTask));

    [HttpPost("{id:guid}/tasks/{taskId:guid}/approve")]
    [ProducesResponseType(typeof(OffboardingTaskDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> ApproveTask(Guid id, Guid taskId, [FromBody] ApproveOffboardingTaskRequest? request, CancellationToken ct) =>
        RunAsync(async a => TaskResult(await _offboardings.ApproveTaskAsync(a, id, taskId, request, ct)), nameof(ApproveTask));

    [HttpPost("{id:guid}/tasks/{taskId:guid}/reject")]
    [ProducesResponseType(typeof(OffboardingTaskDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> RejectTask(Guid id, Guid taskId, [FromBody] RejectOffboardingTaskRequest request, CancellationToken ct) =>
        RunAsync(async a => TaskResult(await _offboardings.RejectTaskAsync(a, id, taskId, request, ct)), nameof(RejectTask));

    /// <summary>Waives a task (reason required; it no longer counts toward progress or clearance). Requires Offboarding.ApproveClearance.</summary>
    [HttpPost("{id:guid}/tasks/{taskId:guid}/waive")]
    [ProducesResponseType(typeof(OffboardingTaskDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> WaiveTask(Guid id, Guid taskId, [FromBody] WaiveOffboardingTaskRequest request, CancellationToken ct) =>
        RunAsync(async a => TaskResult(await _offboardings.WaiveTaskAsync(a, id, taskId, request, ct)), nameof(WaiveTask));

    // -------------------------------------------------------------------------
    // Assets
    // -------------------------------------------------------------------------

    [HttpGet("{id:guid}/assets")]
    [ProducesResponseType(typeof(IReadOnlyList<OffboardingAssetDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> ListAssets(Guid id, CancellationToken ct) =>
        RunAsync(async a => Ok(await _offboardings.ListAssetsAsync(a, id, ct)), nameof(ListAssets));

    [HttpPost("{id:guid}/assets")]
    [ProducesResponseType(typeof(OffboardingAssetDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> AddAsset(Guid id, [FromBody] CreateOffboardingAssetRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var asset = await _offboardings.AddAssetAsync(a, id, request, ct);
            return Created($"/api/offboardings/{id}/assets/{asset.Id}", asset);
        }, nameof(AddAsset));

    [HttpPut("{id:guid}/assets/{assetId:guid}")]
    [ProducesResponseType(typeof(OffboardingAssetDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> UpdateAsset(Guid id, Guid assetId, [FromBody] UpdateOffboardingAssetRequest request, CancellationToken ct) =>
        RunAsync(async a => Found(await _offboardings.UpdateAssetAsync(a, id, assetId, request, ct), "Asset"), nameof(UpdateAsset));

    [HttpDelete("{id:guid}/assets/{assetId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> DeleteAsset(Guid id, Guid assetId, CancellationToken ct) =>
        RunAsync(async a => await _offboardings.DeleteAssetAsync(a, id, assetId, ct) ? NoContent() : NotFoundResult("Asset"), nameof(DeleteAsset));

    /// <summary>Marks an asset Returned / Damaged / Missing / Waived (waiving requires a reason).</summary>
    [HttpPost("{id:guid}/assets/{assetId:guid}/status")]
    [ProducesResponseType(typeof(OffboardingAssetDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> UpdateAssetStatus(Guid id, Guid assetId, [FromBody] UpdateOffboardingAssetStatusRequest request, CancellationToken ct) =>
        RunAsync(async a => Found(await _offboardings.UpdateAssetStatusAsync(a, id, assetId, request, ct), "Asset"), nameof(UpdateAssetStatus));

    // -------------------------------------------------------------------------
    // Access
    // -------------------------------------------------------------------------

    [HttpGet("{id:guid}/access")]
    [ProducesResponseType(typeof(IReadOnlyList<OffboardingAccessItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> ListAccessItems(Guid id, CancellationToken ct) =>
        RunAsync(async a => Ok(await _offboardings.ListAccessItemsAsync(a, id, ct)), nameof(ListAccessItems));

    [HttpPost("{id:guid}/access")]
    [ProducesResponseType(typeof(OffboardingAccessItemDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> AddAccessItem(Guid id, [FromBody] CreateOffboardingAccessItemRequest request, CancellationToken ct) =>
        RunAsync(async a =>
        {
            var item = await _offboardings.AddAccessItemAsync(a, id, request, ct);
            return Created($"/api/offboardings/{id}/access/{item.Id}", item);
        }, nameof(AddAccessItem));

    [HttpPut("{id:guid}/access/{accessItemId:guid}")]
    [ProducesResponseType(typeof(OffboardingAccessItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> UpdateAccessItem(Guid id, Guid accessItemId, [FromBody] UpdateOffboardingAccessItemRequest request, CancellationToken ct) =>
        RunAsync(async a => Found(await _offboardings.UpdateAccessItemAsync(a, id, accessItemId, request, ct), "Access item"), nameof(UpdateAccessItem));

    [HttpDelete("{id:guid}/access/{accessItemId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> DeleteAccessItem(Guid id, Guid accessItemId, CancellationToken ct) =>
        RunAsync(async a => await _offboardings.DeleteAccessItemAsync(a, id, accessItemId, ct) ? NoContent() : NotFoundResult("Access item"), nameof(DeleteAccessItem));

    /// <summary>Marks access Scheduled / Revoked / Verified / NotApplicable (Verified is only allowed after Revoked).</summary>
    [HttpPost("{id:guid}/access/{accessItemId:guid}/status")]
    [ProducesResponseType(typeof(OffboardingAccessItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> UpdateAccessItemStatus(Guid id, Guid accessItemId, [FromBody] UpdateOffboardingAccessStatusRequest request, CancellationToken ct) =>
        RunAsync(async a => Found(await _offboardings.UpdateAccessItemStatusAsync(a, id, accessItemId, request, ct), "Access item"), nameof(UpdateAccessItemStatus));

    // -------------------------------------------------------------------------
    // Settlement
    // -------------------------------------------------------------------------

    /// <summary>Settlement checklist. Amounts and notes are withheld without ManageSettlement / ViewConfidentialInformation.</summary>
    [HttpGet("{id:guid}/settlement")]
    [ProducesResponseType(typeof(IReadOnlyList<OffboardingSettlementItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> ListSettlementItems(Guid id, CancellationToken ct) =>
        RunAsync(async a => Ok(await _offboardings.ListSettlementItemsAsync(a, id, ct)), nameof(ListSettlementItems));

    [HttpPut("{id:guid}/settlement/{itemId:guid}")]
    [ProducesResponseType(typeof(OffboardingSettlementItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> UpdateSettlementItem(Guid id, Guid itemId, [FromBody] UpdateOffboardingSettlementItemRequest request, CancellationToken ct) =>
        RunAsync(async a => Found(await _offboardings.UpdateSettlementItemAsync(a, id, itemId, request, ct), "Settlement item"), nameof(UpdateSettlementItem));

    // -------------------------------------------------------------------------
    // Exit interview
    // -------------------------------------------------------------------------

    /// <summary>Exit interview. Responses and HR notes are withheld without ViewConfidentialInformation.</summary>
    [HttpGet("{id:guid}/exit-interview")]
    [ProducesResponseType(typeof(OffboardingExitInterviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> GetExitInterview(Guid id, CancellationToken ct) =>
        RunAsync(async a => Found(await _offboardings.GetExitInterviewAsync(a, id, ct), "Exit interview"), nameof(GetExitInterview));

    [HttpPut("{id:guid}/exit-interview")]
    [ProducesResponseType(typeof(OffboardingExitInterviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> UpdateExitInterview(Guid id, [FromBody] UpdateOffboardingExitInterviewRequest request, CancellationToken ct) =>
        RunAsync(async a => Found(await _offboardings.UpdateExitInterviewAsync(a, id, request, ct), "Exit interview"), nameof(UpdateExitInterview));

    // -------------------------------------------------------------------------
    // Documents
    // -------------------------------------------------------------------------

    [HttpGet("{id:guid}/documents")]
    [ProducesResponseType(typeof(IReadOnlyList<OffboardingDocumentDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> ListDocuments(Guid id, CancellationToken ct) =>
        RunAsync(async a => Ok(await _offboardings.ListDocumentsAsync(a, id, ct)), nameof(ListDocuments));

    [HttpPut("{id:guid}/documents/{documentId:guid}")]
    [ProducesResponseType(typeof(OffboardingDocumentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> UpdateDocument(Guid id, Guid documentId, [FromBody] UpdateOffboardingDocumentRequest request, CancellationToken ct) =>
        RunAsync(async a => Found(await _offboardings.UpdateDocumentAsync(a, id, documentId, request, ct), "Document"), nameof(UpdateDocument));

    // -------------------------------------------------------------------------
    // Notification settings / log
    // -------------------------------------------------------------------------

    [HttpGet("notification-settings")]
    [ProducesResponseType(typeof(IReadOnlyList<OffboardingNotificationSettingDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> GetNotificationSettings(CancellationToken ct) =>
        RunAsync(async a => Ok(await _offboardings.GetNotificationSettingsAsync(a, ct)), nameof(GetNotificationSettings));

    [HttpPut("notification-settings")]
    [ProducesResponseType(typeof(IReadOnlyList<OffboardingNotificationSettingDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> UpdateNotificationSettings([FromBody] UpdateOffboardingNotificationSettingsRequest request, CancellationToken ct) =>
        RunAsync(async a => Ok(await _offboardings.UpdateNotificationSettingsAsync(a, request, ct)), nameof(UpdateNotificationSettings));

    /// <summary>Recent notification attempts (Queued / Sent / Failed / Skipped).</summary>
    [HttpGet("notifications")]
    [ProducesResponseType(typeof(IReadOnlyList<OffboardingNotificationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> ListNotifications([FromQuery] Guid? offboardingId, [FromQuery] int take = 50, CancellationToken ct = default) =>
        RunAsync(async a => Ok(await _offboardings.ListNotificationsAsync(a, offboardingId, take, ct)), nameof(ListNotifications));

    // -------------------------------------------------------------------------

    private IActionResult Detail(OffboardingDetailDto? detail) => detail == null ? NotFoundResult("Offboarding") : Ok(detail);

    private IActionResult TaskResult(OffboardingTaskDto? task) => task == null ? NotFoundResult("Task") : Ok(task);

    private IActionResult Found<T>(T? value, string what) where T : class => value == null ? NotFoundResult(what) : Ok(value);
}
