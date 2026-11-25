namespace nexthire_api.DTOs.CoreHr;

// -----------------------------------------------------------------------------
// Constants
// -----------------------------------------------------------------------------

public static class OffboardingStatuses
{
    public const string Scheduled = "Scheduled";
    public const string InProgress = "InProgress";
    public const string PendingClearance = "PendingClearance";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        Scheduled, InProgress, PendingClearance, Completed, Cancelled
    };

    /// <summary>Statuses that still count as an "active" offboarding (one per employee).</summary>
    public static readonly HashSet<string> Active = new(StringComparer.OrdinalIgnoreCase)
    {
        Scheduled, InProgress, PendingClearance
    };

    /// <summary>Kanban pipeline columns, in display order (Cancelled is excluded).</summary>
    public static readonly IReadOnlyList<(string Id, string Label)> PipelineColumns =
    [
        (Scheduled, "Scheduled"),
        (InProgress, "In Progress"),
        (PendingClearance, "Pending Clearance"),
        (Completed, "Completed")
    ];
}

public static class SeparationTypes
{
    public const string VoluntaryResignation = "VoluntaryResignation";
    public const string InvoluntaryTermination = "InvoluntaryTermination";
    public const string ContractExpiration = "ContractExpiration";
    public const string Retirement = "Retirement";
    public const string Layoff = "Layoff";
    public const string MutualAgreement = "MutualAgreement";
    public const string Other = "Other";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        VoluntaryResignation, InvoluntaryTermination, ContractExpiration, Retirement, Layoff, MutualAgreement, Other
    };

    /// <summary>Employer-initiated separations: the employee is never notified automatically for these.</summary>
    public static readonly HashSet<string> Involuntary = new(StringComparer.OrdinalIgnoreCase)
    {
        InvoluntaryTermination, Layoff
    };

    /// <summary>Human-readable label (written to the employee's termination_reason, never the confidential reason).</summary>
    public static string Label(string? type) => type switch
    {
        VoluntaryResignation => "Voluntary resignation",
        InvoluntaryTermination => "Involuntary termination",
        ContractExpiration => "Contract expiration",
        Retirement => "Retirement",
        Layoff => "Layoff",
        MutualAgreement => "Mutual agreement",
        _ => "Separation"
    };
}

public static class RehireEligibilities
{
    public const string Eligible = "Eligible";
    public const string NotEligible = "NotEligible";
    public const string Conditional = "Conditional";
    public const string Unknown = "Unknown";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        Eligible, NotEligible, Conditional, Unknown
    };
}

public static class OffboardingTaskStatuses
{
    public const string Pending = "Pending";
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
    public const string Blocked = "Blocked";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        Pending, InProgress, Completed, Cancelled, Blocked
    };

    /// <summary>Statuses a caller may set directly via task update (Completed/Cancelled have dedicated operations).</summary>
    public static readonly HashSet<string> Settable = new(StringComparer.OrdinalIgnoreCase)
    {
        Pending, InProgress, Blocked
    };
}

public static class OffboardingApprovalStatuses
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase) { Pending, Approved, Rejected };
}

public static class OffboardingTaskCategories
{
    public const string HRAdministration = "HRAdministration";
    public const string Documentation = "Documentation";
    public const string KnowledgeTransfer = "KnowledgeTransfer";
    public const string ITSecurity = "ITSecurity";
    public const string EquipmentAssets = "EquipmentAssets";
    public const string FinancePayroll = "FinancePayroll";
    public const string Compliance = "Compliance";
    public const string ExitInterview = "ExitInterview";
    public const string ManagementApproval = "ManagementApproval";
    public const string Other = "Other";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        HRAdministration, Documentation, KnowledgeTransfer, ITSecurity, EquipmentAssets,
        FinancePayroll, Compliance, ExitInterview, ManagementApproval, Other
    };
}

public static class OffboardingAssignmentTypes
{
    public const string Employee = "Employee";
    public const string DirectManager = "DirectManager";
    public const string HR = "HR";
    public const string IT = "IT";
    public const string Finance = "Finance";
    public const string SpecificUser = "SpecificUser";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        Employee, DirectManager, HR, IT, Finance, SpecificUser
    };
}

public static class OffboardingDocumentStatuses
{
    public const string Missing = "Missing";
    public const string Uploaded = "Uploaded";
    public const string PendingApproval = "PendingApproval";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        Missing, Uploaded, PendingApproval, Approved, Rejected
    };
}

public static class OffboardingAssetStatuses
{
    public const string PendingReturn = "PendingReturn";
    public const string Returned = "Returned";
    public const string Damaged = "Damaged";
    public const string Missing = "Missing";
    public const string Waived = "Waived";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        PendingReturn, Returned, Damaged, Missing, Waived
    };
}

public static class OffboardingAccessStatuses
{
    public const string Pending = "Pending";
    public const string Scheduled = "Scheduled";
    public const string Revoked = "Revoked";
    public const string Verified = "Verified";
    public const string NotApplicable = "NotApplicable";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        Pending, Scheduled, Revoked, Verified, NotApplicable
    };
}

public static class OffboardingSettlementStatuses
{
    public const string Pending = "Pending";
    public const string Completed = "Completed";
    public const string NotApplicable = "NotApplicable";
    public const string Waived = "Waived";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        Pending, Completed, NotApplicable, Waived
    };
}

public static class OffboardingExitInterviewStatuses
{
    public const string NotScheduled = "NotScheduled";
    public const string Scheduled = "Scheduled";
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string Skipped = "Skipped";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        NotScheduled, Scheduled, InProgress, Completed, Skipped
    };
}

/// <summary>Notification trigger codes (nh_hr_offboarding_notification_settings.trigger_code).</summary>
public static class OffboardingTriggers
{
    public const string OffboardingCreated = "OffboardingCreated";
    public const string OffboardingStarted = "OffboardingStarted";
    public const string OffboardingPendingClearance = "OffboardingPendingClearance";
    public const string OffboardingCompleted = "OffboardingCompleted";
    public const string OffboardingCancelled = "OffboardingCancelled";
    public const string TaskAssigned = "TaskAssigned";
    public const string TaskDueSoon = "TaskDueSoon";
    public const string TaskOverdue = "TaskOverdue";
    public const string TaskApprovalRequested = "TaskApprovalRequested";
    public const string TaskApproved = "TaskApproved";
    public const string TaskRejected = "TaskRejected";
    public const string LastWorkingDayApproaching = "LastWorkingDayApproaching";
    public const string AssetReturnOverdue = "AssetReturnOverdue";

    public static readonly IReadOnlyList<string> All =
    [
        OffboardingCreated, OffboardingStarted, OffboardingPendingClearance, OffboardingCompleted, OffboardingCancelled,
        TaskAssigned, TaskDueSoon, TaskOverdue, TaskApprovalRequested, TaskApproved, TaskRejected,
        LastWorkingDayApproaching, AssetReturnOverdue
    ];
}

/// <summary>Recipient role tokens stored in notification settings (recipients_json).</summary>
public static class OffboardingRecipientRoles
{
    public const string Owner = "Owner";
    public const string Assignee = "Assignee";
    /// <summary>Only resolved when the offboarding has notify_employee = 1 and the separation is not involuntary.</summary>
    public const string Employee = "Employee";
    public const string DirectManager = "DirectManager";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        Owner, Assignee, Employee, DirectManager
    };
}

// -----------------------------------------------------------------------------
// Gate state contracts (pure rules in OffboardingValidation)
// -----------------------------------------------------------------------------

/// <summary>Minimal task shape needed by the pure progress / transition rules in OffboardingValidation.</summary>
public interface IOffboardingTaskState
{
    Guid Id { get; }
    string Title { get; }
    string Status { get; }
    bool IsWaived { get; }
    bool IsRequired { get; }
    bool RequiresApproval { get; }
    string? ApprovalStatus { get; }
    DateTime? DueDate { get; }
    Guid? DependsOnTaskId { get; }
}

public interface IOffboardingAssetState
{
    string AssetName { get; }
    bool ReturnRequired { get; }
    string Status { get; }
}

public interface IOffboardingAccessState
{
    string SystemName { get; }
    string Status { get; }
}

public interface IOffboardingSettlementState
{
    string Title { get; }
    bool IsRequired { get; }
    string Status { get; }
}

/// <summary>Lightweight task row used to compute progress / overdue counts for list, pipeline and summary queries.</summary>
public class OffboardingTaskStateRow : IOffboardingTaskState
{
    public Guid Id { get; set; }
    public Guid OffboardingId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = OffboardingTaskStatuses.Pending;
    public bool IsWaived { get; set; }
    public bool IsRequired { get; set; } = true;
    public bool RequiresApproval { get; set; }
    public string? ApprovalStatus { get; set; }
    public DateTime? DueDate { get; set; }
    public Guid? DependsOnTaskId { get; set; }
}

// -----------------------------------------------------------------------------
// Templates
// -----------------------------------------------------------------------------

public class OffboardingTemplateTaskDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Category { get; set; } = OffboardingTaskCategories.Other;
    public int DisplayOrder { get; set; }
    public string AssignmentType { get; set; } = OffboardingAssignmentTypes.HR;
    public Guid? DefaultAssigneeUserId { get; set; }
    /// <summary>Days relative to the last working day (negative = before).</summary>
    public int? DueOffsetDays { get; set; }
    public bool IsRequired { get; set; } = true;
    public bool RequiresDocument { get; set; }
    public string? DocumentCategory { get; set; }
    public bool RequiresApproval { get; set; }
    public Guid? DependsOnTaskId { get; set; }
}

public class OffboardingTemplateListItemDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? SeparationType { get; set; }
    public Guid? DepartmentId { get; set; }
    public string? Department { get; set; }
    public Guid? JobTitleId { get; set; }
    public string? JobTitle { get; set; }
    public Guid? EmploymentTypeId { get; set; }
    public string? EmploymentType { get; set; }
    public int? EstimatedDurationDays { get; set; }
    public bool IsActive { get; set; }
    public bool IsDefault { get; set; }
    public int Version { get; set; }
    public int TaskCount { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public class OffboardingTemplateDetailDto : OffboardingTemplateListItemDto
{
    public List<OffboardingTemplateTaskDto> Tasks { get; set; } = new();
}

public class OffboardingTemplateListQuery
{
    public string? Search { get; set; }
    public Guid? DepartmentId { get; set; }
    public string? SeparationType { get; set; }
    public bool? IsActive { get; set; }
    public bool IncludeArchived { get; set; }
}

public class UpsertOffboardingTemplateTaskRequest
{
    /// <summary>
    /// Optional client-side reference used only to express <see cref="DependsOnTaskId"/> inside this payload.
    /// Tasks are always re-created with fresh ids when a template is saved.
    /// </summary>
    public Guid? Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Category { get; set; } = OffboardingTaskCategories.Other;
    public string AssignmentType { get; set; } = OffboardingAssignmentTypes.HR;
    public Guid? DefaultAssigneeUserId { get; set; }
    public int? DueOffsetDays { get; set; }
    public bool IsRequired { get; set; } = true;
    public bool RequiresDocument { get; set; }
    public string? DocumentCategory { get; set; }
    public bool RequiresApproval { get; set; }
    /// <summary>References another task's <see cref="Id"/> in the same payload.</summary>
    public Guid? DependsOnTaskId { get; set; }
}

public class UpsertOffboardingTemplateRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? SeparationType { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? JobTitleId { get; set; }
    public Guid? EmploymentTypeId { get; set; }
    public int? EstimatedDurationDays { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDefault { get; set; }
    /// <summary>Tasks in display order. On update the existing task list is replaced.</summary>
    public List<UpsertOffboardingTemplateTaskRequest> Tasks { get; set; } = new();
}

public class DuplicateOffboardingTemplateRequest
{
    public string? Name { get; set; }
}

// -----------------------------------------------------------------------------
// Offboardings
// -----------------------------------------------------------------------------

public class OffboardingListItemDto
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public string EmployeeNumber { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string? EmployeeEmail { get; set; }
    public string? AvatarUrl { get; set; }
    public string? JobTitle { get; set; }
    public Guid? JobTitleId { get; set; }
    public string? Department { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? TemplateId { get; set; }
    public string? TemplateName { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string? OwnerName { get; set; }
    public string SeparationType { get; set; } = SeparationTypes.Other;
    public DateTime? NoticeDate { get; set; }
    public DateTime? ProcessStartDate { get; set; }
    public DateTime? LastWorkingDay { get; set; }
    public DateTime? TerminationEffectiveDate { get; set; }
    public DateTime? ExpectedCompletionDate { get; set; }
    public DateTime? ActualCompletionDate { get; set; }
    public string Status { get; set; } = OffboardingStatuses.Scheduled;
    /// <summary>completedApplicable / totalApplicable * 100 (waived/cancelled excluded; approval-gated tasks need Approved).</summary>
    public double ProgressPercent { get; set; }
    public int TotalTasks { get; set; }
    public int CompletedTasks { get; set; }
    public int OverdueCount { get; set; }
    /// <summary>Assets that must still be returned (required, not Returned / Waived).</summary>
    public int PendingAssets { get; set; }
    /// <summary>Access items not yet Revoked / Verified / NotApplicable.</summary>
    public int PendingAccessItems { get; set; }
    public bool EmploymentStatusApplied { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class OffboardingDetailDto : OffboardingListItemDto
{
    public int? TemplateVersion { get; set; }
    public string? RehireEligibility { get; set; }
    public bool NotifyEmployee { get; set; }
    /// <summary>Confidential: null unless the caller has Offboarding.ViewConfidentialInformation.</summary>
    public string? SeparationReason { get; set; }
    /// <summary>Confidential: null unless the caller has Offboarding.ViewConfidentialInformation.</summary>
    public string? ConfidentialNotes { get; set; }
    /// <summary>True when confidential fields were withheld because the caller lacks the capability.</summary>
    public bool ConfidentialRedacted { get; set; }
    public string? CancelReason { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    /// <summary>True when every required, non-waived task is Completed (and approved where needed).</summary>
    public bool CanSubmitClearance { get; set; }
    /// <summary>True when clearance gates plus asset, access and settlement gates are all satisfied.</summary>
    public bool CanComplete { get; set; }
    /// <summary>Required tasks blocking submission for clearance.</summary>
    public List<string> ClearanceBlockers { get; set; } = new();
    /// <summary>Everything still blocking completion (tasks, assets, access, settlement).</summary>
    public List<string> CompletionBlockers { get; set; } = new();
    public List<OffboardingTaskDto> Tasks { get; set; } = new();
    public List<OffboardingAssetDto> Assets { get; set; } = new();
    public List<OffboardingAccessItemDto> AccessItems { get; set; } = new();
    public List<OffboardingSettlementItemDto> SettlementItems { get; set; } = new();
    public OffboardingExitInterviewDto? ExitInterview { get; set; }
    public List<OffboardingDocumentDto> Documents { get; set; } = new();
}

public class OffboardingTaskDto : IOffboardingTaskState
{
    public Guid Id { get; set; }
    public Guid OffboardingId { get; set; }
    public Guid? TemplateTaskId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Category { get; set; } = OffboardingTaskCategories.Other;
    public string AssignmentType { get; set; } = OffboardingAssignmentTypes.HR;
    public Guid? AssignedUserId { get; set; }
    public string? AssignedUserName { get; set; }
    public DateTime? DueDate { get; set; }
    public string Status { get; set; } = OffboardingTaskStatuses.Pending;
    public string? Priority { get; set; }
    public bool IsRequired { get; set; } = true;
    public bool RequiresDocument { get; set; }
    public string? DocumentCategory { get; set; }
    public bool RequiresApproval { get; set; }
    public string? ApprovalStatus { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? CompletedBy { get; set; }
    public string? CompletionNotes { get; set; }
    public string? EvidenceUrl { get; set; }
    public int DisplayOrder { get; set; }
    public Guid? DependsOnTaskId { get; set; }
    public Guid? AtsTaskId { get; set; }
    public bool IsWaived { get; set; }
    public string? WaiveReason { get; set; }
    public bool IsOverdue { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class OffboardingAssetDto : IOffboardingAssetState
{
    public Guid Id { get; set; }
    public Guid OffboardingId { get; set; }
    public string AssetName { get; set; } = string.Empty;
    public string? AssetType { get; set; }
    public string? SerialNumber { get; set; }
    public bool ReturnRequired { get; set; } = true;
    public DateTime? ExpectedReturnDate { get; set; }
    public DateTime? ActualReturnDate { get; set; }
    public string? ConditionNotes { get; set; }
    public Guid? ReceivedBy { get; set; }
    public string Status { get; set; } = OffboardingAssetStatuses.PendingReturn;
    public string? Notes { get; set; }
    public string? EvidenceUrl { get; set; }
    public string? WaiveReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public class OffboardingAccessItemDto : IOffboardingAccessState
{
    public Guid Id { get; set; }
    public Guid OffboardingId { get; set; }
    public string SystemName { get; set; } = string.Empty;
    public Guid? ResponsibleUserId { get; set; }
    public string? ResponsibleUserName { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string Status { get; set; } = OffboardingAccessStatuses.Pending;
    public string? EvidenceUrl { get; set; }
    public Guid? VerifiedBy { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public class OffboardingSettlementItemDto : IOffboardingSettlementState
{
    public Guid Id { get; set; }
    public Guid OffboardingId { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public bool IsRequired { get; set; } = true;
    public string Status { get; set; } = OffboardingSettlementStatuses.Pending;
    /// <summary>Withheld (null) unless the caller has ManageSettlement or ViewConfidentialInformation.</summary>
    public decimal? Amount { get; set; }
    public string? Currency { get; set; }
    public string? Notes { get; set; }
    public string? EvidenceUrl { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? CompletedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class OffboardingExitInterviewDto
{
    public Guid Id { get; set; }
    public Guid OffboardingId { get; set; }
    public Guid? InterviewerUserId { get; set; }
    public string? InterviewerName { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public string Status { get; set; } = OffboardingExitInterviewStatuses.NotScheduled;
    public string? QuestionnaireJson { get; set; }
    /// <summary>Confidential: null unless the caller has Offboarding.ViewConfidentialInformation.</summary>
    public string? ResponsesJson { get; set; }
    /// <summary>Confidential: null unless the caller has Offboarding.ViewConfidentialInformation.</summary>
    public string? HrNotes { get; set; }
    public bool ConfidentialRedacted { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public class OffboardingDocumentDto
{
    public Guid Id { get; set; }
    public Guid OffboardingId { get; set; }
    public Guid? OffboardingTaskId { get; set; }
    public Guid? EmployeeDocumentId { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
    public string Status { get; set; } = OffboardingDocumentStatuses.Missing;
    public DateTimeOffset CreatedAt { get; set; }
}

public class OffboardingActivityDto
{
    public Guid Id { get; set; }
    public Guid OffboardingId { get; set; }
    public string ActivityType { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string? DetailsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public string? CreatedByName { get; set; }
}

public class OffboardingListQuery
{
    public string? Search { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public Guid? TemplateId { get; set; }
    public string? Status { get; set; }
    public string? SeparationType { get; set; }
    public DateTime? LastWorkingFrom { get; set; }
    public DateTime? LastWorkingTo { get; set; }
    public bool OverdueOnly { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    /// <summary>employee | lastWorkingDay | expectedCompletion | status | created</summary>
    public string Sort { get; set; } = "lastWorkingDay";
    public string Dir { get; set; } = "asc";
}

public class OffboardingPipelineQuery
{
    public string? Search { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public Guid? TemplateId { get; set; }
    public string? SeparationType { get; set; }
    public DateTime? LastWorkingFrom { get; set; }
    public DateTime? LastWorkingTo { get; set; }
    public bool OverdueOnly { get; set; }
    /// <summary>Completed offboardings older than this many days are left out of the Completed column (default 60).</summary>
    public int CompletedWithinDays { get; set; } = 60;
}

// -----------------------------------------------------------------------------
// Requests
// -----------------------------------------------------------------------------

public class CreateOffboardingTaskRequest
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

public class CreateOffboardingRequest
{
    public Guid EmployeeId { get; set; }
    public string SeparationType { get; set; } = string.Empty;
    /// <summary>Confidential. Ignored without Offboarding.ViewConfidentialInformation.</summary>
    public string? SeparationReason { get; set; }
    public DateTime? NoticeDate { get; set; }
    public DateTime? LastWorkingDay { get; set; }
    /// <summary>Defaults to the last working day.</summary>
    public DateTime? TerminationEffectiveDate { get; set; }
    public DateTime? ExpectedCompletionDate { get; set; }
    public string? RehireEligibility { get; set; }
    /// <summary>Confidential. Ignored without Offboarding.ViewConfidentialInformation.</summary>
    public string? ConfidentialNotes { get; set; }
    /// <summary>Optional. When omitted the best matching active template (job title, department, separation type, default) is used.</summary>
    public Guid? TemplateId { get; set; }
    public Guid? OwnerUserId { get; set; }
    /// <summary>Explicit opt-in. Never defaulted and not allowed for involuntary separations.</summary>
    public bool NotifyEmployee { get; set; }
    /// <summary>When true the offboarding is created directly as InProgress instead of Scheduled.</summary>
    public bool StartImmediately { get; set; }
    /// <summary>Template tasks to leave out of this offboarding (ignored without Offboarding.ManageTasks).</summary>
    public List<Guid>? ExcludedTemplateTaskIds { get; set; }
    /// <summary>Extra one-off tasks (ignored without Offboarding.ManageTasks).</summary>
    public List<CreateOffboardingTaskRequest>? AdditionalTasks { get; set; }
    /// <summary>Company assets to be returned (ignored without Offboarding.ManageAssets). Empty by default.</summary>
    public List<CreateOffboardingAssetRequest>? Assets { get; set; }
    /// <summary>Overrides the default access system list (ignored without Offboarding.ManageAccess).</summary>
    public List<string>? AccessSystems { get; set; }
}

public class StartOffboardingForEmployeeRequest
{
    public string? SeparationType { get; set; }
    public string? SeparationReason { get; set; }
    public DateTime? NoticeDate { get; set; }
    public DateTime? LastWorkingDay { get; set; }
    public DateTime? TerminationEffectiveDate { get; set; }
    public Guid? TemplateId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public bool NotifyEmployee { get; set; }
    public bool StartImmediately { get; set; }
}

public class UpdateOffboardingRequest
{
    public Guid? OwnerUserId { get; set; }
    public string? SeparationType { get; set; }
    /// <summary>Confidential. Ignored (existing value kept) without Offboarding.ViewConfidentialInformation.</summary>
    public string? SeparationReason { get; set; }
    public DateTime? NoticeDate { get; set; }
    public DateTime? LastWorkingDay { get; set; }
    public DateTime? TerminationEffectiveDate { get; set; }
    public DateTime? ExpectedCompletionDate { get; set; }
    public string? RehireEligibility { get; set; }
    /// <summary>Confidential. Ignored (existing value kept) without Offboarding.ViewConfidentialInformation.</summary>
    public string? ConfidentialNotes { get; set; }
    /// <summary>Null keeps the current value (reset to false when the type changes to an involuntary one).</summary>
    public bool? NotifyEmployee { get; set; }
}

public class ChangeOffboardingStatusRequest
{
    public string Status { get; set; } = string.Empty;
    /// <summary>Required when moving to Cancelled.</summary>
    public string? Reason { get; set; }
    public string? Notes { get; set; }
}

public class CancelOffboardingRequest
{
    public string Reason { get; set; } = string.Empty;
}

public class CompleteOffboardingRequest
{
    /// <summary>Defaults to today (UTC). Cannot be in the future.</summary>
    public DateTime? CompletionDate { get; set; }
    public string? Notes { get; set; }
}

public class UpdateOffboardingTaskRequest
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
    /// <summary>Ignored without Offboarding.ApproveClearance (prevents bypassing approvals).</summary>
    public bool? RequiresApproval { get; set; }
    /// <summary>Pending, InProgress or Blocked. Null leaves the status unchanged.</summary>
    public string? Status { get; set; }
    /// <summary>Ignored without Offboarding.ApproveClearance. Null leaves the flag unchanged.</summary>
    public bool? IsWaived { get; set; }
    /// <summary>Required when <see cref="IsWaived"/> is true.</summary>
    public string? WaiveReason { get; set; }
}

public class CompleteOffboardingTaskRequest
{
    public string? Notes { get; set; }
    public string? EvidenceUrl { get; set; }
}

public class ApproveOffboardingTaskRequest
{
    public string? Notes { get; set; }
}

public class RejectOffboardingTaskRequest
{
    public string Reason { get; set; } = string.Empty;
}

public class WaiveOffboardingTaskRequest
{
    public string Reason { get; set; } = string.Empty;
}

public class UpdateOffboardingDocumentRequest
{
    public string Status { get; set; } = string.Empty;
    public Guid? EmployeeDocumentId { get; set; }
}

public class CreateOffboardingAssetRequest
{
    public string AssetName { get; set; } = string.Empty;
    public string? AssetType { get; set; }
    public string? SerialNumber { get; set; }
    public bool ReturnRequired { get; set; } = true;
    public DateTime? ExpectedReturnDate { get; set; }
    public string? Notes { get; set; }
}

public class UpdateOffboardingAssetRequest
{
    public string AssetName { get; set; } = string.Empty;
    public string? AssetType { get; set; }
    public string? SerialNumber { get; set; }
    public bool ReturnRequired { get; set; } = true;
    public DateTime? ExpectedReturnDate { get; set; }
    public string? Notes { get; set; }
}

public class UpdateOffboardingAssetStatusRequest
{
    public string Status { get; set; } = string.Empty;
    /// <summary>Defaults to today when moving to Returned.</summary>
    public DateTime? ActualReturnDate { get; set; }
    public string? ConditionNotes { get; set; }
    public string? EvidenceUrl { get; set; }
    /// <summary>Required when moving to Waived.</summary>
    public string? WaiveReason { get; set; }
    public string? Notes { get; set; }
}

public class CreateOffboardingAccessItemRequest
{
    public string SystemName { get; set; } = string.Empty;
    public Guid? ResponsibleUserId { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public string? Notes { get; set; }
}

public class UpdateOffboardingAccessItemRequest
{
    public string SystemName { get; set; } = string.Empty;
    public Guid? ResponsibleUserId { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public string? Notes { get; set; }
}

public class UpdateOffboardingAccessStatusRequest
{
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset? ScheduledAt { get; set; }
    public string? EvidenceUrl { get; set; }
    public string? Notes { get; set; }
}

public class UpdateOffboardingSettlementItemRequest
{
    public string Status { get; set; } = string.Empty;
    public decimal? Amount { get; set; }
    public string? Currency { get; set; }
    public string? Notes { get; set; }
    public string? EvidenceUrl { get; set; }
}

public class UpdateOffboardingExitInterviewRequest
{
    public Guid? InterviewerUserId { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public string Status { get; set; } = OffboardingExitInterviewStatuses.NotScheduled;
    public string? QuestionnaireJson { get; set; }
    /// <summary>Confidential. Ignored (existing value kept) without Offboarding.ViewConfidentialInformation.</summary>
    public string? ResponsesJson { get; set; }
    /// <summary>Confidential. Ignored (existing value kept) without Offboarding.ViewConfidentialInformation.</summary>
    public string? HrNotes { get; set; }
}

// -----------------------------------------------------------------------------
// Pipeline / summary
// -----------------------------------------------------------------------------

public class OffboardingPipelineCardDto
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string? JobTitle { get; set; }
    public string? Department { get; set; }
    public string? AvatarUrl { get; set; }
    public string SeparationType { get; set; } = SeparationTypes.Other;
    public DateTime? LastWorkingDay { get; set; }
    public DateTime? ExpectedCompletion { get; set; }
    public double ProgressPercent { get; set; }
    public int OverdueCount { get; set; }
    public int PendingAssets { get; set; }
    public int PendingAccessItems { get; set; }
    public string? OwnerName { get; set; }
    public string? TemplateName { get; set; }
}

public class OffboardingPipelineColumnDto
{
    /// <summary>Status id (Scheduled, InProgress, PendingClearance, Completed).</summary>
    public string Status { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; }
    public List<OffboardingPipelineCardDto> Cards { get; set; } = new();
}

public class OffboardingPipelineDto
{
    public List<OffboardingPipelineColumnDto> Columns { get; set; } = new();
}

public class OffboardingSummaryDto
{
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public int ActiveOffboardings { get; set; }
    public int Scheduled { get; set; }
    public int InProgress { get; set; }
    public int PendingClearance { get; set; }
    public int StartedInPeriod { get; set; }
    public int PreviousStartedInPeriod { get; set; }
    public int CompletedInPeriod { get; set; }
    public int PreviousCompletedInPeriod { get; set; }
    public double AverageCompletionDays { get; set; }
    public double PreviousAverageCompletionDays { get; set; }
    public double AverageProgressPercent { get; set; }
    public int OverdueTasks { get; set; }
    public int OverdueOffboardings { get; set; }
    /// <summary>Active offboardings whose last working day falls within the next 7 days.</summary>
    public int UpcomingDepartures { get; set; }
    public int PendingAssetReturns { get; set; }
    public int PendingAccessRevocations { get; set; }
    public Dictionary<string, int> ByStatus { get; set; } = new();
    public Dictionary<string, int> BySeparationType { get; set; } = new();
    public Dictionary<string, int> ByDepartment { get; set; } = new();
}

// -----------------------------------------------------------------------------
// Notifications
// -----------------------------------------------------------------------------

public class OffboardingNotificationSettingDto
{
    public string TriggerCode { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool ChannelEmail { get; set; }
    public bool ChannelWhatsapp { get; set; }
    public int? DaysBefore { get; set; }
    /// <summary>Recipient role tokens: Owner, Assignee, Employee, DirectManager.</summary>
    public List<string> Recipients { get; set; } = new();
    public DateTimeOffset? UpdatedAt { get; set; }
}

public class UpdateOffboardingNotificationSettingRequest
{
    public string TriggerCode { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool ChannelEmail { get; set; }
    public bool ChannelWhatsapp { get; set; }
    public int? DaysBefore { get; set; }
    public List<string>? Recipients { get; set; }
}

public class UpdateOffboardingNotificationSettingsRequest
{
    public List<UpdateOffboardingNotificationSettingRequest> Settings { get; set; } = new();
}

public class OffboardingNotificationDto
{
    public Guid Id { get; set; }
    public Guid? OffboardingId { get; set; }
    public string TriggerCode { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public string? Recipient { get; set; }
    /// <summary>Queued, Sent, Failed or Skipped.</summary>
    public string Status { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Resolved notification recipient (internal to the notification pipeline).</summary>
public sealed record OffboardingNotificationRecipient(string Role, string? Email, string? Name);
