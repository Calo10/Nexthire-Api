using nexthire_api.DTOs;
using nexthire_api.DTOs.CoreHr;

namespace nexthire_api.Repositories;

/// <summary>Minimal offboarding header used for permission / transition checks.</summary>
public sealed class OffboardingHeader
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string Status { get; set; } = OffboardingStatuses.Scheduled;
    public string SeparationType { get; set; } = SeparationTypes.Other;
    public DateTime? NoticeDate { get; set; }
    public DateTime? ProcessStartDate { get; set; }
    public DateTime? LastWorkingDay { get; set; }
    public DateTime? TerminationEffectiveDate { get; set; }
    public DateTime? ExpectedCompletionDate { get; set; }
    public Guid? OwnerUserId { get; set; }
    public Guid? TemplateId { get; set; }
    public bool NotifyEmployee { get; set; }
    public bool EmploymentStatusApplied { get; set; }
}

/// <summary>Employee facts needed to start an offboarding.</summary>
public sealed class OffboardingEmployeeInfo
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public bool IsArchived { get; set; }
    public string EmploymentStatus { get; set; } = string.Empty;
    public Guid? ApplicationId { get; set; }
    public DateTime? HireDate { get; set; }
}

/// <summary>Repository-level task definition used for both template snapshots and custom tasks.</summary>
public sealed class OffboardingTaskSpec
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Category { get; set; } = OffboardingTaskCategories.Other;
    public string AssignmentType { get; set; } = OffboardingAssignmentTypes.SpecificUser;
    public Guid? AssignedUserId { get; set; }
    public DateTime? DueDate { get; set; }
    public bool IsRequired { get; set; } = true;
    public bool RequiresDocument { get; set; }
    public string? DocumentCategory { get; set; }
    public bool RequiresApproval { get; set; }
    public Guid? DependsOnTaskId { get; set; }
}

public sealed class OffboardingAssetSpec
{
    public string AssetName { get; set; } = string.Empty;
    public string? AssetType { get; set; }
    public string? SerialNumber { get; set; }
    public bool ReturnRequired { get; set; } = true;
    public DateTime? ExpectedReturnDate { get; set; }
    public string? Notes { get; set; }
}

public sealed class OffboardingAccessSpec
{
    public string SystemName { get; set; } = string.Empty;
    public Guid? ResponsibleUserId { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public string? Notes { get; set; }
}

public sealed class UpdateOffboardingCommand
{
    public Guid? OwnerUserId { get; set; }
    public string SeparationType { get; set; } = SeparationTypes.Other;
    public string? SeparationReason { get; set; }
    public DateTime? NoticeDate { get; set; }
    public DateTime? LastWorkingDay { get; set; }
    public DateTime? TerminationEffectiveDate { get; set; }
    public DateTime? ExpectedCompletionDate { get; set; }
    public string? RehireEligibility { get; set; }
    public string? ConfidentialNotes { get; set; }
    public bool NotifyEmployee { get; set; }
    /// <summary>When false the stored separation_reason / confidential_notes are left untouched.</summary>
    public bool UpdateConfidential { get; set; }
}

public sealed class CreateOffboardingCommand
{
    public Guid EmployeeId { get; set; }
    public Guid? TemplateId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string SeparationType { get; set; } = SeparationTypes.Other;
    public string? SeparationReason { get; set; }
    public DateTime? NoticeDate { get; set; }
    public DateTime? LastWorkingDay { get; set; }
    public DateTime? TerminationEffectiveDate { get; set; }
    public DateTime? ExpectedCompletionDate { get; set; }
    public string? RehireEligibility { get; set; }
    public string? ConfidentialNotes { get; set; }
    public bool NotifyEmployee { get; set; }
    /// <summary>Scheduled or InProgress.</summary>
    public string InitialStatus { get; set; } = OffboardingStatuses.Scheduled;
    public IReadOnlyCollection<Guid> ExcludedTemplateTaskIds { get; set; } = Array.Empty<Guid>();
    public IReadOnlyList<OffboardingTaskSpec> AdditionalTasks { get; set; } = Array.Empty<OffboardingTaskSpec>();
    public IReadOnlyList<OffboardingAssetSpec> Assets { get; set; } = Array.Empty<OffboardingAssetSpec>();
    /// <summary>Null = the default access system list.</summary>
    public IReadOnlyList<string>? AccessSystems { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? ActorName { get; set; }
}

public sealed record CreatedOffboarding(Guid Id, IReadOnlyList<Guid> TaskIds);

/// <summary>
/// Dapper/SQL Server persistence for Core HR offboarding. All queries are parameterized and scoped by organization_id.
/// User ids (owner / assignee) are nh_users ids; actor ids written to created_by / updated_by columns are Nexa user ids.
/// </summary>
public interface IOffboardingRepository
{
    // Schema / defaults / demo data
    Task EnsureSchemaAsync();

    /// <summary>Seeds the four default templates when the org has none, plus default notification settings.</summary>
    Task EnsureOrgDefaultsAsync(Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>Development only: creates a few offboardings (various statuses) for existing demo employees when none exist.</summary>
    Task SeedDemoOffboardingsIfEmptyAsync(Guid organizationId, Guid? actorUserId, bool isDevelopment, CancellationToken cancellationToken = default);

    // Templates
    Task<IReadOnlyList<OffboardingTemplateListItemDto>> ListTemplatesAsync(Guid organizationId, OffboardingTemplateListQuery query, CancellationToken cancellationToken = default);
    Task<OffboardingTemplateDetailDto?> GetTemplateAsync(Guid organizationId, Guid templateId, CancellationToken cancellationToken = default);
    Task<Guid> CreateTemplateAsync(Guid organizationId, UpsertOffboardingTemplateRequest request, Guid? actorUserId, CancellationToken cancellationToken = default);

    /// <summary>Updates the template and replaces its tasks; bumps the version. Returns false when not found / archived.</summary>
    Task<bool> UpdateTemplateAsync(Guid organizationId, Guid templateId, UpsertOffboardingTemplateRequest request, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<Guid?> DuplicateTemplateAsync(Guid organizationId, Guid templateId, string newName, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<bool> ArchiveTemplateAsync(Guid organizationId, Guid templateId, Guid? actorUserId, CancellationToken cancellationToken = default);

    /// <summary>Makes the (active, non-archived) template the org default and clears the flag on all others.</summary>
    Task<bool> SetDefaultTemplateAsync(Guid organizationId, Guid templateId, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<bool> TemplateNameExistsAsync(Guid organizationId, string name, Guid? excludeId, CancellationToken cancellationToken = default);

    /// <summary>Returns an error message when a department / job title / employment type does not belong to the org.</summary>
    Task<string?> CheckTemplateReferencesAsync(Guid organizationId, UpsertOffboardingTemplateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Best matching active template: job title, then department, then separation type, then org default.</summary>
    Task<Guid?> ResolveTemplateIdForEmployeeAsync(Guid organizationId, Guid employeeId, string separationType, CancellationToken cancellationToken = default);

    // Offboardings
    Task<PagedResult<OffboardingListItemDto>> ListAsync(Guid organizationId, OffboardingListQuery query, CancellationToken cancellationToken = default);
    Task<OffboardingPipelineDto> PipelineAsync(Guid organizationId, OffboardingPipelineQuery query, CancellationToken cancellationToken = default);

    /// <summary>Full detail with all confidential fields populated; the service redacts according to the caller's capabilities.</summary>
    Task<OffboardingDetailDto?> GetAsync(Guid organizationId, Guid offboardingId, CancellationToken cancellationToken = default);
    Task<OffboardingHeader?> GetHeaderAsync(Guid organizationId, Guid offboardingId, CancellationToken cancellationToken = default);
    Task<OffboardingEmployeeInfo?> GetEmployeeInfoAsync(Guid organizationId, Guid employeeId, CancellationToken cancellationToken = default);
    Task<Guid?> FindActiveOffboardingIdAsync(Guid organizationId, Guid employeeId, CancellationToken cancellationToken = default);
    Task<bool> OrgUserExistsAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the offboarding in one transaction: template tasks snapshotted, document requirements, assets,
    /// default access items, settlement checklist, exit interview (NotScheduled) and activity.
    /// </summary>
    Task<CreatedOffboarding> CreateOffboardingAsync(Guid organizationId, CreateOffboardingCommand command, CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(Guid organizationId, Guid offboardingId, UpdateOffboardingCommand command, Guid? actorUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compare-and-set status change (only applies when the current status equals <paramref name="from"/>).
    /// Sets the process start date on first start, actual completion date on completion, and the cancel reason on cancellation.
    /// </summary>
    Task<bool> TransitionStatusAsync(
        Guid organizationId, Guid offboardingId, string from, string to,
        DateTime? completionDate, string? cancelReason, Guid? actorUserId, CancellationToken cancellationToken = default);

    // Employment status (separation -> employee record)

    /// <summary>
    /// When the offboarding's termination effective date is today or earlier and employment_status_applied = 0 (and it is not
    /// cancelled), marks the employee Terminated (termination date / reason from the separation type), writes employment
    /// history and employee activity, and sets employment_status_applied = 1 — all in one transaction.
    /// Idempotent. Returns true when this call applied it.
    /// </summary>
    Task<bool> ApplyEmploymentStatusAsync(Guid organizationId, Guid offboardingId, Guid? actorUserId, string? actorName, CancellationToken cancellationToken = default);

    /// <summary>Applies every due, not-yet-applied termination of the org. Safe to call from any request or a future background job.</summary>
    Task<int> ApplyDueTerminationsAsync(Guid organizationId, Guid? actorUserId, string? actorName, CancellationToken cancellationToken = default);

    // Tasks
    Task<IReadOnlyList<OffboardingTaskDto>> ListTasksAsync(Guid organizationId, Guid offboardingId, CancellationToken cancellationToken = default);
    Task<OffboardingTaskDto?> GetTaskAsync(Guid organizationId, Guid offboardingId, Guid taskId, CancellationToken cancellationToken = default);
    Task<Guid> AddTaskAsync(Guid organizationId, Guid offboardingId, OffboardingTaskSpec task, CancellationToken cancellationToken = default);
    Task<bool> UpdateTaskAsync(Guid organizationId, Guid offboardingId, Guid taskId, OffboardingTaskSpec task, string? status, bool? isWaived, string? waiveReason, CancellationToken cancellationToken = default);
    Task<bool> CancelTaskAsync(Guid organizationId, Guid offboardingId, Guid taskId, CancellationToken cancellationToken = default);
    Task<bool> CompleteTaskAsync(Guid organizationId, Guid offboardingId, Guid taskId, string? notes, string? evidenceUrl, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<bool> ReopenTaskAsync(Guid organizationId, Guid offboardingId, Guid taskId, CancellationToken cancellationToken = default);
    Task<bool> ApproveTaskAsync(Guid organizationId, Guid offboardingId, Guid taskId, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<bool> RejectTaskAsync(Guid organizationId, Guid offboardingId, Guid taskId, string reason, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<bool> WaiveTaskAsync(Guid organizationId, Guid offboardingId, Guid taskId, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Best-effort: mirrors assigned offboarding tasks into dbo.tasks (source_type='offboarding') and stores ats_task_id.
    /// Failures (e.g. a NOT NULL application_id) are swallowed so offboarding is never blocked by the ATS link.
    /// </summary>
    Task LinkAtsTasksAsync(Guid organizationId, Guid offboardingId, CancellationToken cancellationToken = default);

    // Assets
    Task<IReadOnlyList<OffboardingAssetDto>> ListAssetsAsync(Guid organizationId, Guid offboardingId, CancellationToken cancellationToken = default);
    Task<OffboardingAssetDto?> GetAssetAsync(Guid organizationId, Guid offboardingId, Guid assetId, CancellationToken cancellationToken = default);
    Task<Guid> AddAssetAsync(Guid organizationId, Guid offboardingId, OffboardingAssetSpec asset, CancellationToken cancellationToken = default);
    Task<bool> UpdateAssetAsync(Guid organizationId, Guid offboardingId, Guid assetId, OffboardingAssetSpec asset, CancellationToken cancellationToken = default);
    Task<bool> DeleteAssetAsync(Guid organizationId, Guid offboardingId, Guid assetId, CancellationToken cancellationToken = default);
    Task<bool> UpdateAssetStatusAsync(Guid organizationId, Guid offboardingId, Guid assetId, string status, UpdateOffboardingAssetStatusRequest request, Guid? actorUserId, CancellationToken cancellationToken = default);

    // Access items
    Task<IReadOnlyList<OffboardingAccessItemDto>> ListAccessItemsAsync(Guid organizationId, Guid offboardingId, CancellationToken cancellationToken = default);
    Task<OffboardingAccessItemDto?> GetAccessItemAsync(Guid organizationId, Guid offboardingId, Guid accessItemId, CancellationToken cancellationToken = default);
    Task<Guid> AddAccessItemAsync(Guid organizationId, Guid offboardingId, OffboardingAccessSpec item, CancellationToken cancellationToken = default);
    Task<bool> UpdateAccessItemAsync(Guid organizationId, Guid offboardingId, Guid accessItemId, OffboardingAccessSpec item, CancellationToken cancellationToken = default);
    Task<bool> DeleteAccessItemAsync(Guid organizationId, Guid offboardingId, Guid accessItemId, CancellationToken cancellationToken = default);
    Task<bool> UpdateAccessItemStatusAsync(Guid organizationId, Guid offboardingId, Guid accessItemId, string status, UpdateOffboardingAccessStatusRequest request, Guid? actorUserId, CancellationToken cancellationToken = default);

    // Settlement
    Task<IReadOnlyList<OffboardingSettlementItemDto>> ListSettlementItemsAsync(Guid organizationId, Guid offboardingId, CancellationToken cancellationToken = default);
    Task<OffboardingSettlementItemDto?> GetSettlementItemAsync(Guid organizationId, Guid offboardingId, Guid itemId, CancellationToken cancellationToken = default);
    Task<bool> UpdateSettlementItemAsync(Guid organizationId, Guid offboardingId, Guid itemId, string status, UpdateOffboardingSettlementItemRequest request, Guid? actorUserId, CancellationToken cancellationToken = default);

    // Exit interview
    Task<OffboardingExitInterviewDto?> GetExitInterviewAsync(Guid organizationId, Guid offboardingId, CancellationToken cancellationToken = default);
    Task<bool> UpdateExitInterviewAsync(Guid organizationId, Guid offboardingId, string status, UpdateOffboardingExitInterviewRequest request, bool updateConfidential, CancellationToken cancellationToken = default);

    // Documents
    Task<IReadOnlyList<OffboardingDocumentDto>> ListDocumentsAsync(Guid organizationId, Guid offboardingId, CancellationToken cancellationToken = default);
    Task<OffboardingDocumentDto?> GetDocumentAsync(Guid organizationId, Guid offboardingId, Guid documentId, CancellationToken cancellationToken = default);
    Task<bool> UpdateDocumentAsync(Guid organizationId, Guid offboardingId, Guid documentId, string status, Guid? employeeDocumentId, CancellationToken cancellationToken = default);

    // Activity / summary
    Task<IReadOnlyList<OffboardingActivityDto>> ListActivityAsync(Guid organizationId, Guid offboardingId, int take, CancellationToken cancellationToken = default);
    Task AddActivityAsync(Guid organizationId, Guid offboardingId, string activityType, string summary, string? detailsJson, Guid? actorUserId, string? actorName, CancellationToken cancellationToken = default);
    Task<OffboardingSummaryDto> GetSummaryAsync(Guid organizationId, DateTime? from, DateTime? to, CancellationToken cancellationToken = default);

    // Notifications
    Task<IReadOnlyList<OffboardingNotificationSettingDto>> GetNotificationSettingsAsync(Guid organizationId, CancellationToken cancellationToken = default);
    Task<OffboardingNotificationSettingDto?> GetNotificationSettingAsync(Guid organizationId, string triggerCode, CancellationToken cancellationToken = default);
    Task UpsertNotificationSettingAsync(Guid organizationId, UpdateOffboardingNotificationSettingRequest setting, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OffboardingNotificationDto>> ListNotificationsAsync(Guid organizationId, Guid? offboardingId, int take, CancellationToken cancellationToken = default);

    /// <summary>Inserts a notification attempt. Returns false (no row written) when the dedupe key already exists for the org.</summary>
    Task<bool> RecordNotificationAttemptAsync(
        Guid organizationId, Guid? offboardingId, string triggerCode, string dedupeKey, string channel,
        string? recipient, string status, string? errorMessage, CancellationToken cancellationToken = default);
    Task UpdateNotificationAttemptAsync(Guid organizationId, string dedupeKey, string status, string? errorMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves recipients for the given role tokens. The Employee role is only resolved when the offboarding has
    /// notify_employee = 1 and the separation is not involuntary.
    /// </summary>
    Task<IReadOnlyList<OffboardingNotificationRecipient>> ResolveNotificationRecipientsAsync(
        Guid organizationId, Guid offboardingId, Guid? taskId, IReadOnlyCollection<string> roles, CancellationToken cancellationToken = default);
}
