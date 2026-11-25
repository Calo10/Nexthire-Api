using nexthire_api.DTOs;
using nexthire_api.DTOs.CoreHr;

namespace nexthire_api.Repositories;

/// <summary>Minimal onboarding header used for permission / transition checks.</summary>
public sealed class OnboardingHeader
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string Status { get; set; } = OnboardingStatuses.NotStarted;
    public DateTime? StartDate { get; set; }
    public DateTime? ExpectedCompletionDate { get; set; }
    public Guid? OwnerUserId { get; set; }
    public Guid? TemplateId { get; set; }
}

/// <summary>Employee facts needed to start an onboarding.</summary>
public sealed class OnboardingEmployeeInfo
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public bool IsArchived { get; set; }
    public Guid? ApplicationId { get; set; }
    public DateTime? HireDate { get; set; }
}

/// <summary>Repository-level task definition used for both template snapshots and custom tasks.</summary>
public sealed class OnboardingTaskSpec
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Category { get; set; } = OnboardingTaskCategories.Other;
    public string AssignmentType { get; set; } = OnboardingAssignmentTypes.SpecificUser;
    public Guid? AssignedUserId { get; set; }
    public DateTime? DueDate { get; set; }
    public bool IsRequired { get; set; } = true;
    public bool RequiresDocument { get; set; }
    public string? DocumentCategory { get; set; }
    public bool RequiresApproval { get; set; }
    public Guid? DependsOnTaskId { get; set; }
}

public sealed class CreateOnboardingCommand
{
    public Guid EmployeeId { get; set; }
    public Guid? TemplateId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? ExpectedCompletionDate { get; set; }
    public string? Notes { get; set; }
    /// <summary>NotStarted or InProgress.</summary>
    public string InitialStatus { get; set; } = OnboardingStatuses.NotStarted;
    public IReadOnlyCollection<Guid> ExcludedTemplateTaskIds { get; set; } = Array.Empty<Guid>();
    public IReadOnlyList<OnboardingTaskSpec> AdditionalTasks { get; set; } = Array.Empty<OnboardingTaskSpec>();
    public Guid? ActorUserId { get; set; }
    public string? ActorName { get; set; }
}

public sealed record CreatedOnboarding(Guid Id, IReadOnlyList<Guid> TaskIds);

/// <summary>
/// Dapper/SQL Server persistence for Core HR onboarding. All queries are parameterized and scoped by organization_id.
/// User ids (owner / assignee) are nh_users ids; actor ids written to created_by / updated_by columns are Nexa user ids.
/// </summary>
public interface IOnboardingRepository
{
    // Schema / defaults / demo data
    Task EnsureSchemaAsync();

    /// <summary>Seeds the General, IT and Hospitality templates when the org has none, and default notification settings.</summary>
    Task EnsureOrgDefaultsAsync(Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>Development only: creates a few onboardings (various statuses) for existing demo employees when none exist.</summary>
    Task SeedDemoOnboardingsIfEmptyAsync(Guid organizationId, Guid? actorUserId, bool isDevelopment, CancellationToken cancellationToken = default);

    // Templates
    Task<IReadOnlyList<OnboardingTemplateListItemDto>> ListTemplatesAsync(Guid organizationId, OnboardingTemplateListQuery query, CancellationToken cancellationToken = default);
    Task<OnboardingTemplateDetailDto?> GetTemplateAsync(Guid organizationId, Guid templateId, CancellationToken cancellationToken = default);
    Task<Guid> CreateTemplateAsync(Guid organizationId, UpsertOnboardingTemplateRequest request, Guid? actorUserId, CancellationToken cancellationToken = default);

    /// <summary>Updates the template and replaces its tasks; bumps the version. Returns false when not found / archived.</summary>
    Task<bool> UpdateTemplateAsync(Guid organizationId, Guid templateId, UpsertOnboardingTemplateRequest request, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<Guid?> DuplicateTemplateAsync(Guid organizationId, Guid templateId, string newName, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<bool> ArchiveTemplateAsync(Guid organizationId, Guid templateId, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<bool> TemplateNameExistsAsync(Guid organizationId, string name, Guid? excludeId, CancellationToken cancellationToken = default);

    /// <summary>Returns an error message when a department / job title / employment type does not belong to the org.</summary>
    Task<string?> CheckTemplateReferencesAsync(Guid organizationId, UpsertOnboardingTemplateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Best matching active template for the employee: job title, then department, then org default.</summary>
    Task<Guid?> ResolveTemplateIdForEmployeeAsync(Guid organizationId, Guid employeeId, CancellationToken cancellationToken = default);

    // Onboardings
    Task<PagedResult<OnboardingListItemDto>> ListAsync(Guid organizationId, OnboardingListQuery query, CancellationToken cancellationToken = default);
    Task<OnboardingPipelineDto> PipelineAsync(Guid organizationId, OnboardingPipelineQuery query, CancellationToken cancellationToken = default);
    Task<OnboardingDetailDto?> GetAsync(Guid organizationId, Guid onboardingId, CancellationToken cancellationToken = default);
    Task<OnboardingHeader?> GetHeaderAsync(Guid organizationId, Guid onboardingId, CancellationToken cancellationToken = default);
    Task<OnboardingEmployeeInfo?> GetEmployeeInfoAsync(Guid organizationId, Guid employeeId, CancellationToken cancellationToken = default);
    Task<Guid?> FindActiveOnboardingIdAsync(Guid organizationId, Guid employeeId, CancellationToken cancellationToken = default);
    Task<bool> OrgUserExistsAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Creates the onboarding in one transaction (template tasks snapshotted, document requirements, activity).</summary>
    Task<CreatedOnboarding> CreateOnboardingAsync(Guid organizationId, CreateOnboardingCommand command, CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(Guid organizationId, Guid onboardingId, UpdateOnboardingRequest request, Guid? actorUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compare-and-set status change (only applies when the current status equals <paramref name="from"/>).
    /// Sets start date on first start, actual completion date on completion, and the cancel reason on cancellation.
    /// </summary>
    Task<bool> TransitionStatusAsync(
        Guid organizationId, Guid onboardingId, string from, string to,
        DateTime? completionDate, string? cancelReason, Guid? actorUserId, CancellationToken cancellationToken = default);

    // Tasks
    Task<IReadOnlyList<OnboardingTaskDto>> ListTasksAsync(Guid organizationId, Guid onboardingId, CancellationToken cancellationToken = default);
    Task<OnboardingTaskDto?> GetTaskAsync(Guid organizationId, Guid onboardingId, Guid taskId, CancellationToken cancellationToken = default);
    Task<Guid> AddTaskAsync(Guid organizationId, Guid onboardingId, OnboardingTaskSpec task, CancellationToken cancellationToken = default);
    Task<bool> UpdateTaskAsync(Guid organizationId, Guid onboardingId, Guid taskId, OnboardingTaskSpec task, string? status, bool? isWaived, CancellationToken cancellationToken = default);
    Task<bool> CancelTaskAsync(Guid organizationId, Guid onboardingId, Guid taskId, CancellationToken cancellationToken = default);
    Task<bool> CompleteTaskAsync(Guid organizationId, Guid onboardingId, Guid taskId, string? notes, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<bool> ReopenTaskAsync(Guid organizationId, Guid onboardingId, Guid taskId, CancellationToken cancellationToken = default);
    Task<bool> ApproveTaskAsync(Guid organizationId, Guid onboardingId, Guid taskId, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<bool> RejectTaskAsync(Guid organizationId, Guid onboardingId, Guid taskId, string reason, Guid? actorUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Best-effort: mirrors assigned onboarding tasks into dbo.tasks (source_type='onboarding') and stores ats_task_id.
    /// Failures (e.g. a NOT NULL application_id) are swallowed so onboarding is never blocked by the ATS link.
    /// </summary>
    Task LinkAtsTasksAsync(Guid organizationId, Guid onboardingId, CancellationToken cancellationToken = default);

    // Documents
    Task<IReadOnlyList<OnboardingDocumentDto>> ListDocumentsAsync(Guid organizationId, Guid onboardingId, CancellationToken cancellationToken = default);
    Task<OnboardingDocumentDto?> GetDocumentAsync(Guid organizationId, Guid onboardingId, Guid documentId, CancellationToken cancellationToken = default);
    Task<bool> UpdateDocumentAsync(Guid organizationId, Guid onboardingId, Guid documentId, string status, Guid? employeeDocumentId, CancellationToken cancellationToken = default);

    // Activity / summary
    Task<IReadOnlyList<OnboardingActivityDto>> ListActivityAsync(Guid organizationId, Guid onboardingId, int take, CancellationToken cancellationToken = default);
    Task AddActivityAsync(Guid organizationId, Guid onboardingId, string activityType, string summary, string? detailsJson, Guid? actorUserId, string? actorName, CancellationToken cancellationToken = default);
    Task<OnboardingSummaryDto> GetSummaryAsync(Guid organizationId, DateTime? from, DateTime? to, CancellationToken cancellationToken = default);

    // Notifications
    Task<IReadOnlyList<OnboardingNotificationSettingDto>> GetNotificationSettingsAsync(Guid organizationId, CancellationToken cancellationToken = default);
    Task<OnboardingNotificationSettingDto?> GetNotificationSettingAsync(Guid organizationId, string triggerCode, CancellationToken cancellationToken = default);
    Task UpsertNotificationSettingAsync(Guid organizationId, UpdateOnboardingNotificationSettingRequest setting, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OnboardingNotificationDto>> ListNotificationsAsync(Guid organizationId, Guid? onboardingId, int take, CancellationToken cancellationToken = default);

    /// <summary>Inserts a notification attempt. Returns false (no row written) when the dedupe key already exists for the org.</summary>
    Task<bool> RecordNotificationAttemptAsync(
        Guid organizationId, Guid? onboardingId, string triggerCode, string dedupeKey, string channel,
        string? recipient, string status, string? errorMessage, CancellationToken cancellationToken = default);
    Task UpdateNotificationAttemptAsync(Guid organizationId, string dedupeKey, string status, string? errorMessage, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OnboardingNotificationRecipient>> ResolveNotificationRecipientsAsync(
        Guid organizationId, Guid onboardingId, Guid? taskId, IReadOnlyCollection<string> roles, CancellationToken cancellationToken = default);
}
