using nexthire_api.DTOs;
using nexthire_api.DTOs.CoreHr;

namespace nexthire_api.Services;

/// <summary>
/// Core HR offboarding operations. Every method resolves the caller's role codes and enforces
/// <see cref="nexthire_api.Security.OffboardingPermissions"/>; a missing capability throws
/// <see cref="nexthire_api.Exceptions.HrAccessDeniedException"/>. Business-rule failures throw
/// <see cref="nexthire_api.Exceptions.EmployeeServiceException"/> (400 validation, 404 missing, 409 conflict).
/// </summary>
/// <remarks>
/// Confidential data (separation reason, confidential notes, exit interview responses / HR notes) is only returned to
/// callers holding <c>Offboarding.ViewConfidentialInformation</c>; everyone else receives nulls plus a
/// <c>ConfidentialRedacted</c> flag, and cannot overwrite those fields. Settlement amounts and notes are only returned to
/// callers with <c>Offboarding.ManageSettlement</c> or <c>Offboarding.ViewConfidentialInformation</c>.
/// The employee record is flipped to Terminated by <see cref="ApplyDueTerminationsAsync"/> (also invoked lazily from
/// list / pipeline / summary / start) and when an offboarding completes — no hosted service is required.
/// </remarks>
public interface IOffboardingService
{
    Task<IReadOnlyList<string>> GetCapabilitiesAsync(HrActor actor, CancellationToken cancellationToken = default);

    // Offboardings: read
    Task<PagedResult<OffboardingListItemDto>> ListAsync(HrActor actor, OffboardingListQuery query, CancellationToken cancellationToken = default);
    Task<OffboardingPipelineDto> GetPipelineAsync(HrActor actor, OffboardingPipelineQuery query, CancellationToken cancellationToken = default);
    Task<OffboardingSummaryDto> GetSummaryAsync(HrActor actor, DateTime? from, DateTime? to, CancellationToken cancellationToken = default);
    Task<OffboardingDetailDto?> GetAsync(HrActor actor, Guid offboardingId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OffboardingActivityDto>> GetActivityAsync(HrActor actor, Guid offboardingId, int take, CancellationToken cancellationToken = default);

    // Offboardings: write
    Task<OffboardingDetailDto> CreateAsync(HrActor actor, CreateOffboardingRequest request, CancellationToken cancellationToken = default);

    /// <summary>Idempotent: returns the employee's active offboarding when one exists, otherwise creates one (template auto-selected).</summary>
    Task<OffboardingDetailDto> StartOffboardingForEmployeeAsync(HrActor actor, Guid employeeId, StartOffboardingForEmployeeRequest? request, CancellationToken cancellationToken = default);
    Task<OffboardingDetailDto?> UpdateAsync(HrActor actor, Guid offboardingId, UpdateOffboardingRequest request, CancellationToken cancellationToken = default);

    /// <summary>Scheduled -> InProgress.</summary>
    Task<OffboardingDetailDto?> StartAsync(HrActor actor, Guid offboardingId, CancellationToken cancellationToken = default);

    /// <summary>InProgress -> PendingClearance (requires all required tasks complete and approved).</summary>
    Task<OffboardingDetailDto?> SubmitClearanceAsync(HrActor actor, Guid offboardingId, CancellationToken cancellationToken = default);

    /// <summary>PendingClearance -> InProgress.</summary>
    Task<OffboardingDetailDto?> ReturnToProgressAsync(HrActor actor, Guid offboardingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// PendingClearance (or InProgress, as the Kanban fast path) -> Completed with identical validation either way:
    /// required tasks, required assets returned / waived, access revoked / verified, required settlement items done.
    /// </summary>
    Task<OffboardingDetailDto?> CompleteAsync(HrActor actor, Guid offboardingId, CompleteOffboardingRequest? request, CancellationToken cancellationToken = default);

    /// <summary>Any active status -> Cancelled. Never restores the employee's employment status.</summary>
    Task<OffboardingDetailDto?> CancelAsync(HrActor actor, Guid offboardingId, CancelOffboardingRequest request, CancellationToken cancellationToken = default);

    /// <summary>Generic transition used by Kanban drag-and-drop; routes through the same validation as the dedicated actions.</summary>
    Task<OffboardingDetailDto?> ChangeStatusAsync(HrActor actor, Guid offboardingId, ChangeOffboardingStatusRequest request, CancellationToken cancellationToken = default);

    // Employment status

    /// <summary>
    /// Background-safe (no caller / permissions): marks employees Terminated for every due, not-yet-applied separation of the
    /// organization. Idempotent. Returns the number of employees updated.
    /// </summary>
    Task<int> ApplyDueTerminationsAsync(Guid organizationId, CancellationToken cancellationToken = default);

    // Tasks
    Task<OffboardingTaskDto> AddTaskAsync(HrActor actor, Guid offboardingId, CreateOffboardingTaskRequest request, CancellationToken cancellationToken = default);
    Task<OffboardingTaskDto?> UpdateTaskAsync(HrActor actor, Guid offboardingId, Guid taskId, UpdateOffboardingTaskRequest request, CancellationToken cancellationToken = default);
    Task<bool> CancelTaskAsync(HrActor actor, Guid offboardingId, Guid taskId, CancellationToken cancellationToken = default);
    Task<OffboardingTaskDto?> CompleteTaskAsync(HrActor actor, Guid offboardingId, Guid taskId, CompleteOffboardingTaskRequest? request, CancellationToken cancellationToken = default);
    Task<OffboardingTaskDto?> ReopenTaskAsync(HrActor actor, Guid offboardingId, Guid taskId, CancellationToken cancellationToken = default);
    Task<OffboardingTaskDto?> ApproveTaskAsync(HrActor actor, Guid offboardingId, Guid taskId, ApproveOffboardingTaskRequest? request, CancellationToken cancellationToken = default);
    Task<OffboardingTaskDto?> RejectTaskAsync(HrActor actor, Guid offboardingId, Guid taskId, RejectOffboardingTaskRequest request, CancellationToken cancellationToken = default);
    Task<OffboardingTaskDto?> WaiveTaskAsync(HrActor actor, Guid offboardingId, Guid taskId, WaiveOffboardingTaskRequest request, CancellationToken cancellationToken = default);

    // Assets
    Task<IReadOnlyList<OffboardingAssetDto>> ListAssetsAsync(HrActor actor, Guid offboardingId, CancellationToken cancellationToken = default);
    Task<OffboardingAssetDto> AddAssetAsync(HrActor actor, Guid offboardingId, CreateOffboardingAssetRequest request, CancellationToken cancellationToken = default);
    Task<OffboardingAssetDto?> UpdateAssetAsync(HrActor actor, Guid offboardingId, Guid assetId, UpdateOffboardingAssetRequest request, CancellationToken cancellationToken = default);
    Task<bool> DeleteAssetAsync(HrActor actor, Guid offboardingId, Guid assetId, CancellationToken cancellationToken = default);
    Task<OffboardingAssetDto?> UpdateAssetStatusAsync(HrActor actor, Guid offboardingId, Guid assetId, UpdateOffboardingAssetStatusRequest request, CancellationToken cancellationToken = default);

    // Access
    Task<IReadOnlyList<OffboardingAccessItemDto>> ListAccessItemsAsync(HrActor actor, Guid offboardingId, CancellationToken cancellationToken = default);
    Task<OffboardingAccessItemDto> AddAccessItemAsync(HrActor actor, Guid offboardingId, CreateOffboardingAccessItemRequest request, CancellationToken cancellationToken = default);
    Task<OffboardingAccessItemDto?> UpdateAccessItemAsync(HrActor actor, Guid offboardingId, Guid accessItemId, UpdateOffboardingAccessItemRequest request, CancellationToken cancellationToken = default);
    Task<bool> DeleteAccessItemAsync(HrActor actor, Guid offboardingId, Guid accessItemId, CancellationToken cancellationToken = default);
    Task<OffboardingAccessItemDto?> UpdateAccessItemStatusAsync(HrActor actor, Guid offboardingId, Guid accessItemId, UpdateOffboardingAccessStatusRequest request, CancellationToken cancellationToken = default);

    // Settlement
    Task<IReadOnlyList<OffboardingSettlementItemDto>> ListSettlementItemsAsync(HrActor actor, Guid offboardingId, CancellationToken cancellationToken = default);
    Task<OffboardingSettlementItemDto?> UpdateSettlementItemAsync(HrActor actor, Guid offboardingId, Guid itemId, UpdateOffboardingSettlementItemRequest request, CancellationToken cancellationToken = default);

    // Exit interview
    Task<OffboardingExitInterviewDto?> GetExitInterviewAsync(HrActor actor, Guid offboardingId, CancellationToken cancellationToken = default);
    Task<OffboardingExitInterviewDto?> UpdateExitInterviewAsync(HrActor actor, Guid offboardingId, UpdateOffboardingExitInterviewRequest request, CancellationToken cancellationToken = default);

    // Documents
    Task<IReadOnlyList<OffboardingDocumentDto>> ListDocumentsAsync(HrActor actor, Guid offboardingId, CancellationToken cancellationToken = default);
    Task<OffboardingDocumentDto?> UpdateDocumentAsync(HrActor actor, Guid offboardingId, Guid documentId, UpdateOffboardingDocumentRequest request, CancellationToken cancellationToken = default);

    // Templates
    Task<IReadOnlyList<OffboardingTemplateListItemDto>> ListTemplatesAsync(HrActor actor, OffboardingTemplateListQuery query, CancellationToken cancellationToken = default);
    Task<OffboardingTemplateDetailDto?> GetTemplateAsync(HrActor actor, Guid templateId, CancellationToken cancellationToken = default);
    Task<OffboardingTemplateDetailDto> CreateTemplateAsync(HrActor actor, UpsertOffboardingTemplateRequest request, CancellationToken cancellationToken = default);
    Task<OffboardingTemplateDetailDto?> UpdateTemplateAsync(HrActor actor, Guid templateId, UpsertOffboardingTemplateRequest request, CancellationToken cancellationToken = default);
    Task<OffboardingTemplateDetailDto?> DuplicateTemplateAsync(HrActor actor, Guid templateId, DuplicateOffboardingTemplateRequest? request, CancellationToken cancellationToken = default);
    Task<bool> ArchiveTemplateAsync(HrActor actor, Guid templateId, CancellationToken cancellationToken = default);
    Task<OffboardingTemplateDetailDto?> SetDefaultTemplateAsync(HrActor actor, Guid templateId, CancellationToken cancellationToken = default);

    // Notifications
    Task<IReadOnlyList<OffboardingNotificationSettingDto>> GetNotificationSettingsAsync(HrActor actor, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OffboardingNotificationSettingDto>> UpdateNotificationSettingsAsync(HrActor actor, UpdateOffboardingNotificationSettingsRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OffboardingNotificationDto>> ListNotificationsAsync(HrActor actor, Guid? offboardingId, int take, CancellationToken cancellationToken = default);
}
