using nexthire_api.DTOs;
using nexthire_api.DTOs.CoreHr;

namespace nexthire_api.Services;

/// <summary>
/// Core HR onboarding operations. Every method resolves the caller's role codes and enforces
/// <see cref="nexthire_api.Security.OnboardingPermissions"/>; a missing capability throws
/// <see cref="nexthire_api.Exceptions.HrAccessDeniedException"/>. Business-rule failures throw
/// <see cref="nexthire_api.Exceptions.EmployeeServiceException"/> (400 validation, 404 missing, 409 conflict).
/// </summary>
/// <remarks>
/// Convert-from-candidate (<see cref="IEmployeeService.ConvertFromCandidateAsync"/>) deliberately does NOT start an
/// onboarding: this keeps the employee and onboarding services free of a circular dependency. The frontend calls
/// <see cref="StartOnboardingForEmployeeAsync"/> (POST api/onboardings/from-employee/{employeeId}) right after a
/// successful convert; that call is idempotent.
/// </remarks>
public interface IOnboardingService
{
    Task<IReadOnlyList<string>> GetCapabilitiesAsync(HrActor actor, CancellationToken cancellationToken = default);

    // Onboardings: read
    Task<PagedResult<OnboardingListItemDto>> ListAsync(HrActor actor, OnboardingListQuery query, CancellationToken cancellationToken = default);
    Task<OnboardingPipelineDto> GetPipelineAsync(HrActor actor, OnboardingPipelineQuery query, CancellationToken cancellationToken = default);
    Task<OnboardingSummaryDto> GetSummaryAsync(HrActor actor, DateTime? from, DateTime? to, CancellationToken cancellationToken = default);
    Task<OnboardingDetailDto?> GetAsync(HrActor actor, Guid onboardingId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OnboardingActivityDto>> GetActivityAsync(HrActor actor, Guid onboardingId, int take, CancellationToken cancellationToken = default);

    // Onboardings: write
    Task<OnboardingDetailDto> CreateAsync(HrActor actor, CreateOnboardingRequest request, CancellationToken cancellationToken = default);

    /// <summary>Idempotent: returns the employee's active onboarding when one exists, otherwise creates one (template auto-selected).</summary>
    Task<OnboardingDetailDto> StartOnboardingForEmployeeAsync(HrActor actor, Guid employeeId, StartOnboardingForEmployeeRequest? request, CancellationToken cancellationToken = default);
    Task<OnboardingDetailDto?> UpdateAsync(HrActor actor, Guid onboardingId, UpdateOnboardingRequest request, CancellationToken cancellationToken = default);

    /// <summary>NotStarted -> InProgress.</summary>
    Task<OnboardingDetailDto?> StartAsync(HrActor actor, Guid onboardingId, CancellationToken cancellationToken = default);

    /// <summary>InProgress -> InReview (requires all required tasks complete and approved).</summary>
    Task<OnboardingDetailDto?> SubmitForReviewAsync(HrActor actor, Guid onboardingId, CancellationToken cancellationToken = default);

    /// <summary>InReview -> InProgress.</summary>
    Task<OnboardingDetailDto?> ReturnToProgressAsync(HrActor actor, Guid onboardingId, CancellationToken cancellationToken = default);

    /// <summary>InReview (or InProgress, as the Kanban fast path) -> Completed with the same validation either way.</summary>
    Task<OnboardingDetailDto?> CompleteAsync(HrActor actor, Guid onboardingId, CompleteOnboardingRequest? request, CancellationToken cancellationToken = default);
    Task<OnboardingDetailDto?> CancelAsync(HrActor actor, Guid onboardingId, CancelOnboardingRequest request, CancellationToken cancellationToken = default);

    /// <summary>Generic transition used by Kanban drag-and-drop; routes through the same validation as the dedicated actions.</summary>
    Task<OnboardingDetailDto?> ChangeStatusAsync(HrActor actor, Guid onboardingId, ChangeOnboardingStatusRequest request, CancellationToken cancellationToken = default);

    // Tasks
    Task<OnboardingTaskDto> AddTaskAsync(HrActor actor, Guid onboardingId, CreateOnboardingTaskRequest request, CancellationToken cancellationToken = default);
    Task<OnboardingTaskDto?> UpdateTaskAsync(HrActor actor, Guid onboardingId, Guid taskId, UpdateOnboardingTaskRequest request, CancellationToken cancellationToken = default);
    Task<bool> CancelTaskAsync(HrActor actor, Guid onboardingId, Guid taskId, CancellationToken cancellationToken = default);
    Task<OnboardingTaskDto?> CompleteTaskAsync(HrActor actor, Guid onboardingId, Guid taskId, CompleteOnboardingTaskRequest? request, CancellationToken cancellationToken = default);
    Task<OnboardingTaskDto?> ReopenTaskAsync(HrActor actor, Guid onboardingId, Guid taskId, CancellationToken cancellationToken = default);
    Task<OnboardingTaskDto?> ApproveTaskAsync(HrActor actor, Guid onboardingId, Guid taskId, ApproveOnboardingTaskRequest? request, CancellationToken cancellationToken = default);
    Task<OnboardingTaskDto?> RejectTaskAsync(HrActor actor, Guid onboardingId, Guid taskId, RejectOnboardingTaskRequest request, CancellationToken cancellationToken = default);

    // Documents
    Task<IReadOnlyList<OnboardingDocumentDto>> ListDocumentsAsync(HrActor actor, Guid onboardingId, CancellationToken cancellationToken = default);
    Task<OnboardingDocumentDto?> UpdateDocumentAsync(HrActor actor, Guid onboardingId, Guid documentId, UpdateOnboardingDocumentRequest request, CancellationToken cancellationToken = default);

    // Templates
    Task<IReadOnlyList<OnboardingTemplateListItemDto>> ListTemplatesAsync(HrActor actor, OnboardingTemplateListQuery query, CancellationToken cancellationToken = default);
    Task<OnboardingTemplateDetailDto?> GetTemplateAsync(HrActor actor, Guid templateId, CancellationToken cancellationToken = default);
    Task<OnboardingTemplateDetailDto> CreateTemplateAsync(HrActor actor, UpsertOnboardingTemplateRequest request, CancellationToken cancellationToken = default);
    Task<OnboardingTemplateDetailDto?> UpdateTemplateAsync(HrActor actor, Guid templateId, UpsertOnboardingTemplateRequest request, CancellationToken cancellationToken = default);
    Task<OnboardingTemplateDetailDto?> DuplicateTemplateAsync(HrActor actor, Guid templateId, DuplicateOnboardingTemplateRequest? request, CancellationToken cancellationToken = default);
    Task<bool> ArchiveTemplateAsync(HrActor actor, Guid templateId, CancellationToken cancellationToken = default);

    // Notifications
    Task<IReadOnlyList<OnboardingNotificationSettingDto>> GetNotificationSettingsAsync(HrActor actor, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OnboardingNotificationSettingDto>> UpdateNotificationSettingsAsync(HrActor actor, UpdateOnboardingNotificationSettingsRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OnboardingNotificationDto>> ListNotificationsAsync(HrActor actor, Guid? onboardingId, int take, CancellationToken cancellationToken = default);
}
