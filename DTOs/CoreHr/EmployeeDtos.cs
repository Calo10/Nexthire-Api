namespace nexthire_api.DTOs.CoreHr;

public static class EmploymentStatuses
{
    public const string Pending = "Pending";
    public const string Onboarding = "Onboarding";
    public const string Active = "Active";
    public const string OnLeave = "OnLeave";
    public const string Inactive = "Inactive";
    public const string Terminated = "Terminated";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        Pending, Onboarding, Active, OnLeave, Inactive, Terminated
    };
}

public class HrLookupDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public bool IsActive { get; set; } = true;
}

public class WorkLocationDto : HrLookupDto
{
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Country { get; set; }
}

public class EmployeeListItemDto
{
    public Guid Id { get; set; }
    public string EmployeeNumber { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? PreferredName { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? ProfilePictureUrl { get; set; }
    public string? WorkEmail { get; set; }
    public string? JobTitle { get; set; }
    public Guid? JobTitleId { get; set; }
    public string? Department { get; set; }
    public Guid? DepartmentId { get; set; }
    public string? Location { get; set; }
    public Guid? WorkLocationId { get; set; }
    public string EmploymentStatus { get; set; } = string.Empty;
    public string? EmploymentType { get; set; }
    public Guid? EmploymentTypeId { get; set; }
    public Guid? ManagerId { get; set; }
    public string? ManagerName { get; set; }
    public DateTime? HireDate { get; set; }
    public bool IsArchived { get; set; }
}

public class EmployeeDetailDto : EmployeeListItemDto
{
    public string? MiddleName { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public string? Nationality { get; set; }
    public string? PersonalEmail { get; set; }
    public string? PersonalPhone { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Country { get; set; }
    public string? PostalCode { get; set; }
    public string? WorkPhone { get; set; }
    public string? WorkModality { get; set; }
    public DateTime? TerminationDate { get; set; }
    public string? TerminationReason { get; set; }
    public DateTime? ProbationEndDate { get; set; }
    public DateTime? ContractStartDate { get; set; }
    public DateTime? ContractEndDate { get; set; }
    public Guid? CandidateId { get; set; }
    public Guid? ApplicationId { get; set; }
    public Guid? LinkedUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public double? TenureYears { get; set; }
    public IReadOnlyList<EmergencyContactDto> EmergencyContacts { get; set; } = Array.Empty<EmergencyContactDto>();
    public IReadOnlyList<EmployeeDocumentDto> UpcomingDocumentExpirations { get; set; } = Array.Empty<EmployeeDocumentDto>();
}

public class CreateEmployeeRequest
{
    public string? EmployeeNumber { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string? MiddleName { get; set; }
    public string LastName { get; set; } = string.Empty;
    public string? PreferredName { get; set; }
    public string? ProfilePictureUrl { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public string? Nationality { get; set; }
    public string? PersonalEmail { get; set; }
    public string? PersonalPhone { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Country { get; set; }
    public string? PostalCode { get; set; }
    public string? WorkEmail { get; set; }
    public string? WorkPhone { get; set; }
    public Guid? JobTitleId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? ManagerId { get; set; }
    public Guid? EmploymentTypeId { get; set; }
    public string? EmploymentStatus { get; set; }
    public DateTime? HireDate { get; set; }
    public Guid? WorkLocationId { get; set; }
    public string? WorkModality { get; set; }
    public DateTime? ProbationEndDate { get; set; }
    public DateTime? ContractStartDate { get; set; }
    public DateTime? ContractEndDate { get; set; }
    public Guid? CandidateId { get; set; }
    public Guid? ApplicationId { get; set; }
    public List<EmergencyContactDto>? EmergencyContacts { get; set; }
}

public class UpdateEmployeeRequest : CreateEmployeeRequest
{
}

public class ChangeEmployeeStatusRequest
{
    public string Status { get; set; } = string.Empty;
    public DateTime? TerminationDate { get; set; }
    public string? TerminationReason { get; set; }
    public string? Notes { get; set; }
}

public class ConvertCandidateToEmployeeRequest
{
    public Guid CandidateId { get; set; }
    public Guid? ApplicationId { get; set; }
    public CreateEmployeeRequest? Overrides { get; set; }
}

public class EmergencyContactDto
{
    public Guid? Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? Relationship { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public bool IsPrimary { get; set; }
}

public class EmployeeDocumentDto
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Guid? DocumentId { get; set; }
    public string? ExternalUrl { get; set; }
    public string Status { get; set; } = "Active";
    public DateTime? ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class UpsertEmployeeDocumentRequest
{
    public string Category { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Guid? DocumentId { get; set; }
    public string? ExternalUrl { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? Status { get; set; }
}

public class EmployeeActivityDto
{
    public Guid Id { get; set; }
    public string ActivityType { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string? DetailsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedByName { get; set; }
}

public class EmploymentHistoryDto
{
    public Guid Id { get; set; }
    public DateTimeOffset EffectiveAt { get; set; }
    public string ChangeType { get; set; } = string.Empty;
    public string? JobTitle { get; set; }
    public string? Department { get; set; }
    public string? EmploymentType { get; set; }
    public string? EmploymentStatus { get; set; }
    public string? Location { get; set; }
    public string? ManagerName { get; set; }
    public string? Notes { get; set; }
    public string? CreatedByName { get; set; }
}

public class CompensationDto
{
    public Guid Id { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string PayFrequency { get; set; } = "Monthly";
    public DateTime EffectiveDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string? Notes { get; set; }
}

public class UpsertCompensationRequest
{
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string PayFrequency { get; set; } = "Monthly";
    public DateTime EffectiveDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string? Notes { get; set; }
}

public class EmployeeSummaryDto
{
    public int TotalEmployees { get; set; }
    public int ActiveEmployees { get; set; }
    public int NewHires { get; set; }
    public int PreviousNewHires { get; set; }
    public double TurnoverRate { get; set; }
    public double PreviousTurnoverRate { get; set; }
    public double AverageTenureYears { get; set; }
    public double PreviousAverageTenureYears { get; set; }
    public Dictionary<string, int> ByStatus { get; set; } = new();
    public Dictionary<string, int> ByDepartment { get; set; } = new();
    public Dictionary<string, int> ByLocation { get; set; } = new();
    public Dictionary<string, int> ByEmploymentType { get; set; } = new();
}

public class EmployeeListQuery
{
    public string? Search { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? JobTitleId { get; set; }
    public string? EmploymentStatus { get; set; }
    public Guid? EmploymentTypeId { get; set; }
    public Guid? WorkLocationId { get; set; }
    public Guid? ManagerId { get; set; }
    public DateTime? HireFrom { get; set; }
    public DateTime? HireTo { get; set; }
    public bool IncludeArchived { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public string Sort { get; set; } = "name";
    public string Dir { get; set; } = "asc";
}

public class EmployeeImportRowError
{
    public int RowNumber { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class EmployeeImportResultDto
{
    public int TotalRows { get; set; }
    public int Created { get; set; }
    public int Skipped { get; set; }
    public IReadOnlyList<EmployeeImportRowError> Errors { get; set; } = Array.Empty<EmployeeImportRowError>();
}

public class CreateHrLookupRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Country { get; set; }
}
