namespace nexthire_api.DTOs.CoreHr;

// -----------------------------------------------------------------------------
// Constants
// -----------------------------------------------------------------------------

public static class OnboardingStatuses
{
    public const string NotStarted = "NotStarted";
    public const string InProgress = "InProgress";
    public const string InReview = "InReview";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        NotStarted, InProgress, InReview, Completed, Cancelled
    };

    /// <summary>Statuses that still count as an "active" onboarding (one per employee).</summary>
    public static readonly HashSet<string> Active = new(StringComparer.OrdinalIgnoreCase)
    {
        NotStarted, InProgress, InReview
    };

    /// <summary>Kanban pipeline columns, in display order (Cancelled is excluded).</summary>
    public static readonly IReadOnlyList<(string Id, string Label)> PipelineColumns =
    [
        (NotStarted, "Not Started"),
        (InProgress, "In Progress"),
        (InReview, "In Review"),
        (Completed, "Completed")
    ];
}

public static class OnboardingTaskStatuses
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

public static class OnboardingApprovalStatuses
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase) { Pending, Approved, Rejected };
}

public static class OnboardingTaskCategories
{
    public const string Documentation = "Documentation";
    public const string Administrative = "Administrative";
    public const string ITEquipment = "ITEquipment";
    public const string Training = "Training";
    public const string Compliance = "Compliance";
    public const string Orientation = "Orientation";
    public const string Meetings = "Meetings";
    public const string Other = "Other";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        Documentation, Administrative, ITEquipment, Training, Compliance, Orientation, Meetings, Other
    };
}

public static class OnboardingAssignmentTypes
{
    public const string Employee = "Employee";
    public const string DirectManager = "DirectManager";
    public const string HR = "HR";
    public const string IT = "IT";
    public const string SpecificUser = "SpecificUser";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        Employee, DirectManager, HR, IT, SpecificUser
    };
}

public static class OnboardingDocumentStatuses
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

/// <summary>Notification trigger codes (nh_hr_onboarding_notification_settings.trigger_code).</summary>
public static class OnboardingTriggers
{
    public const string OnboardingCreated = "OnboardingCreated";
    public const string OnboardingStarted = "OnboardingStarted";
    public const string OnboardingInReview = "OnboardingInReview";
    public const string OnboardingCompleted = "OnboardingCompleted";
    public const string OnboardingCancelled = "OnboardingCancelled";
    public const string TaskAssigned = "TaskAssigned";
    public const string TaskDueSoon = "TaskDueSoon";
    public const string TaskOverdue = "TaskOverdue";
    public const string TaskApprovalRequested = "TaskApprovalRequested";
    public const string TaskApproved = "TaskApproved";
    public const string TaskRejected = "TaskRejected";

    public static readonly IReadOnlyList<string> All =
    [
        OnboardingCreated, OnboardingStarted, OnboardingInReview, OnboardingCompleted, OnboardingCancelled,
        TaskAssigned, TaskDueSoon, TaskOverdue, TaskApprovalRequested, TaskApproved, TaskRejected
    ];
}

/// <summary>Recipient role tokens stored in notification settings (recipients_json).</summary>
public static class OnboardingRecipientRoles
{
    public const string Owner = "Owner";
    public const string Assignee = "Assignee";
    public const string Employee = "Employee";
    public const string DirectManager = "DirectManager";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        Owner, Assignee, Employee, DirectManager
    };
}

/// <summary>Minimal task shape needed by the pure progress / transition rules in OnboardingValidation.</summary>
public interface IOnboardingTaskState
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

/// <summary>Lightweight task row used to compute progress / overdue counts for list, pipeline and summary queries.</summary>
public class OnboardingTaskStateRow : IOnboardingTaskState
{
    public Guid Id { get; set; }
    public Guid OnboardingId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = OnboardingTaskStatuses.Pending;
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

public class OnboardingTemplateTaskDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Category { get; set; } = OnboardingTaskCategories.Other;
    public int DisplayOrder { get; set; }
    public string AssignmentType { get; set; } = OnboardingAssignmentTypes.HR;
    public Guid? DefaultAssigneeUserId { get; set; }
    public int? DueOffsetDays { get; set; }
    public bool IsRequired { get; set; } = true;
    public bool RequiresDocument { get; set; }
    public string? DocumentCategory { get; set; }
    public bool RequiresApproval { get; set; }
    public Guid? DependsOnTaskId { get; set; }
}

public class OnboardingTemplateListItemDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
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

public class OnboardingTemplateDetailDto : OnboardingTemplateListItemDto
{
    public List<OnboardingTemplateTaskDto> Tasks { get; set; } = new();
}

public class OnboardingTemplateListQuery
{
    public string? Search { get; set; }
    public Guid? DepartmentId { get; set; }
    public bool? IsActive { get; set; }
    public bool IncludeArchived { get; set; }
}

public class UpsertOnboardingTemplateTaskRequest
{
    /// <summary>
    /// Optional client-side reference used only to express <see cref="DependsOnTaskId"/> inside this payload.
    /// Tasks are always re-created with fresh ids when a template is saved.
    /// </summary>
    public Guid? Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Category { get; set; } = OnboardingTaskCategories.Other;
    public string AssignmentType { get; set; } = OnboardingAssignmentTypes.HR;
    public Guid? DefaultAssigneeUserId { get; set; }
    public int? DueOffsetDays { get; set; }
    public bool IsRequired { get; set; } = true;
    public bool RequiresDocument { get; set; }
    public string? DocumentCategory { get; set; }
    public bool RequiresApproval { get; set; }
    /// <summary>References another task's <see cref="Id"/> in the same payload.</summary>
    public Guid? DependsOnTaskId { get; set; }
}

public class UpsertOnboardingTemplateRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? JobTitleId { get; set; }
    public Guid? EmploymentTypeId { get; set; }
    public int? EstimatedDurationDays { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDefault { get; set; }
    /// <summary>Tasks in display order. On update the existing task list is replaced.</summary>
    public List<UpsertOnboardingTemplateTaskRequest> Tasks { get; set; } = new();
}

public class DuplicateOnboardingTemplateRequest
{
    public string? Name { get; set; }
}

// -----------------------------------------------------------------------------
// Onboardings
// -----------------------------------------------------------------------------

public class OnboardingListItemDto
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
    public DateTime? StartDate { get; set; }
    public DateTime? ExpectedCompletionDate { get; set; }
    public DateTime? ActualCompletionDate { get; set; }
    public string Status { get; set; } = OnboardingStatuses.NotStarted;
    /// <summary>completedApplicable / totalApplicable * 100 (waived/cancelled excluded; approval-gated tasks need Approved).</summary>
    public double ProgressPercent { get; set; }
    public int TotalTasks { get; set; }
    public int CompletedTasks { get; set; }
    public int OverdueCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class OnboardingDetailDto : OnboardingListItemDto
{
    public int? TemplateVersion { get; set; }
    public string? Notes { get; set; }
    public string? CancelReason { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    /// <summary>True when every required, non-waived task is Completed (and approved where needed).</summary>
    public bool CanComplete { get; set; }
    /// <summary>Titles of required tasks still blocking completion.</summary>
    public List<string> CompletionBlockers { get; set; } = new();
    public List<OnboardingTaskDto> Tasks { get; set; } = new();
    public List<OnboardingDocumentDto> Documents { get; set; } = new();
}

public class OnboardingTaskDto : IOnboardingTaskState
{
    public Guid Id { get; set; }
    public Guid OnboardingId { get; set; }
    public Guid? TemplateTaskId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Category { get; set; } = OnboardingTaskCategories.Other;
    public string AssignmentType { get; set; } = OnboardingAssignmentTypes.HR;
    public Guid? AssignedUserId { get; set; }
    public string? AssignedUserName { get; set; }
    public DateTime? DueDate { get; set; }
    public string Status { get; set; } = OnboardingTaskStatuses.Pending;
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
    public int DisplayOrder { get; set; }
    public Guid? DependsOnTaskId { get; set; }
    public Guid? AtsTaskId { get; set; }
    public bool IsWaived { get; set; }
    public bool IsOverdue { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class OnboardingDocumentDto
{
    public Guid Id { get; set; }
    public Guid OnboardingId { get; set; }
    public Guid? OnboardingTaskId { get; set; }
    public Guid? EmployeeDocumentId { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
    public string Status { get; set; } = OnboardingDocumentStatuses.Missing;
    public DateTimeOffset CreatedAt { get; set; }
}

public class OnboardingActivityDto
{
    public Guid Id { get; set; }
    public Guid OnboardingId { get; set; }
    public string ActivityType { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string? DetailsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public string? CreatedByName { get; set; }
}

public class OnboardingListQuery
{
    public string? Search { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public Guid? TemplateId { get; set; }
    public string? Status { get; set; }
    public DateTime? StartFrom { get; set; }
    public DateTime? StartTo { get; set; }
    public bool OverdueOnly { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    /// <summary>employee | startDate | expectedCompletion | status | created</summary>
    public string Sort { get; set; } = "startDate";
    public string Dir { get; set; } = "desc";
}

public class OnboardingPipelineQuery
{
    public string? Search { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public Guid? TemplateId { get; set; }
    public DateTime? StartFrom { get; set; }
    public DateTime? StartTo { get; set; }
    public bool OverdueOnly { get; set; }
    /// <summary>Completed onboardings older than this many days are left out of the Completed column (default 60).</summary>
    public int CompletedWithinDays { get; set; } = 60;
}

public class CreateOnboardingTaskRequest
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

public class CreateOnboardingRequest
{
    public Guid EmployeeId { get; set; }
    /// <summary>Optional. When omitted the best matching active template (job title, department, then default) is used.</summary>
    public Guid? TemplateId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? ExpectedCompletionDate { get; set; }
    public string? Notes { get; set; }
    /// <summary>When true the onboarding is created directly as InProgress instead of NotStarted.</summary>
    public bool StartImmediately { get; set; }
    /// <summary>Template tasks to leave out of this onboarding (ignored without Onboarding.ManageTasks).</summary>
    public List<Guid>? ExcludedTemplateTaskIds { get; set; }
    /// <summary>Extra one-off tasks (ignored without Onboarding.ManageTasks).</summary>
    public List<CreateOnboardingTaskRequest>? AdditionalTasks { get; set; }
}

public class StartOnboardingForEmployeeRequest
{
    public Guid? TemplateId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public DateTime? StartDate { get; set; }
    public bool StartImmediately { get; set; }
}

public class UpdateOnboardingRequest
{
    public Guid? OwnerUserId { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? ExpectedCompletionDate { get; set; }
    public string? Notes { get; set; }
}

public class ChangeOnboardingStatusRequest
{
    public string Status { get; set; } = string.Empty;
    /// <summary>Required when moving to Cancelled.</summary>
    public string? Reason { get; set; }
    public string? Notes { get; set; }
}

public class CancelOnboardingRequest
{
    public string Reason { get; set; } = string.Empty;
}

public class CompleteOnboardingRequest
{
    /// <summary>Defaults to today (UTC). Cannot be in the future.</summary>
    public DateTime? CompletionDate { get; set; }
    public string? Notes { get; set; }
}

public class UpdateOnboardingTaskRequest
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
    /// <summary>Ignored without Onboarding.ApproveTasks (prevents bypassing approvals).</summary>
    public bool? RequiresApproval { get; set; }
    /// <summary>Pending, InProgress or Blocked. Null leaves the status unchanged.</summary>
    public string? Status { get; set; }
    /// <summary>Ignored without Onboarding.ApproveTasks. Null leaves the flag unchanged.</summary>
    public bool? IsWaived { get; set; }
}

public class CompleteOnboardingTaskRequest
{
    public string? Notes { get; set; }
}

public class ApproveOnboardingTaskRequest
{
    public string? Notes { get; set; }
}

public class RejectOnboardingTaskRequest
{
    public string Reason { get; set; } = string.Empty;
}

public class UpdateOnboardingDocumentRequest
{
    public string Status { get; set; } = string.Empty;
    public Guid? EmployeeDocumentId { get; set; }
}

// -----------------------------------------------------------------------------
// Pipeline / summary
// -----------------------------------------------------------------------------

public class OnboardingPipelineCardDto
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string? JobTitle { get; set; }
    public string? Department { get; set; }
    public string? AvatarUrl { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? ExpectedCompletion { get; set; }
    public double ProgressPercent { get; set; }
    public int OverdueCount { get; set; }
    public string? OwnerName { get; set; }
    public string? TemplateName { get; set; }
}

public class OnboardingPipelineColumnDto
{
    /// <summary>Status id (NotStarted, InProgress, InReview, Completed).</summary>
    public string Status { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; }
    public List<OnboardingPipelineCardDto> Cards { get; set; } = new();
}

public class OnboardingPipelineDto
{
    public List<OnboardingPipelineColumnDto> Columns { get; set; } = new();
}

public class OnboardingSummaryDto
{
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public int ActiveOnboardings { get; set; }
    public int NotStarted { get; set; }
    public int InProgress { get; set; }
    public int InReview { get; set; }
    public int StartedInPeriod { get; set; }
    public int PreviousStartedInPeriod { get; set; }
    public int CompletedInPeriod { get; set; }
    public int PreviousCompletedInPeriod { get; set; }
    public double AverageCompletionDays { get; set; }
    public double PreviousAverageCompletionDays { get; set; }
    public double AverageProgressPercent { get; set; }
    public int OverdueTasks { get; set; }
    public int OverdueOnboardings { get; set; }
    public int UpcomingStarts { get; set; }
    public Dictionary<string, int> ByStatus { get; set; } = new();
    public Dictionary<string, int> ByDepartment { get; set; } = new();
}

// -----------------------------------------------------------------------------
// Notifications
// -----------------------------------------------------------------------------

public class OnboardingNotificationSettingDto
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

public class UpdateOnboardingNotificationSettingRequest
{
    public string TriggerCode { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool ChannelEmail { get; set; }
    public bool ChannelWhatsapp { get; set; }
    public int? DaysBefore { get; set; }
    public List<string>? Recipients { get; set; }
}

public class UpdateOnboardingNotificationSettingsRequest
{
    public List<UpdateOnboardingNotificationSettingRequest> Settings { get; set; } = new();
}

public class OnboardingNotificationDto
{
    public Guid Id { get; set; }
    public Guid? OnboardingId { get; set; }
    public string TriggerCode { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public string? Recipient { get; set; }
    /// <summary>Queued, Sent, Failed or Skipped.</summary>
    public string Status { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Resolved notification recipient (internal to the notification pipeline).</summary>
public sealed record OnboardingNotificationRecipient(string Role, string? Email, string? Name);
