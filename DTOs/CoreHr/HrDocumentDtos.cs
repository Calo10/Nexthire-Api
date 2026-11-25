using Microsoft.AspNetCore.Http;

namespace nexthire_api.DTOs.CoreHr;

/// <summary>Canonical Core HR document categories used by the Documents hub.</summary>
public static class HrDocumentCategories
{
    public const string Contract = "Contract";
    public const string Identification = "Identification";
    public const string Certification = "Certification";
    public const string Medical = "Medical";
    public const string Administrative = "Administrative";
    public const string Legal = "Legal";
    public const string Tax = "Tax";
    public const string Policy = "Policy";
    public const string Other = "Other";

    public static readonly IReadOnlyList<string> All =
    [
        Contract, Identification, Certification, Medical, Administrative, Legal, Tax, Policy, Other
    ];
}

/// <summary>
/// Display statuses computed from the stored status + expiry date. Stored statuses remain
/// Active / Expired / Archived / Pending; "Expiring" is virtual (Active and expires within the window).
/// </summary>
public static class HrDocumentDisplayStatuses
{
    public const string Active = "Active";
    public const string Expiring = "Expiring";
    public const string Expired = "Expired";
    public const string Pending = "Pending";

    public static readonly IReadOnlyList<string> All = [Active, Expiring, Expired, Pending];

    /// <summary>Days before expires_at at which an active document counts as "Expiring".</summary>
    public const int ExpiringWindowDays = 30;
}

public static class HrDocumentRequestStatuses
{
    public const string Open = "Open";
    public const string Fulfilled = "Fulfilled";
    public const string Cancelled = "Cancelled";
    public const string Overdue = "Overdue";
}

public class HrDocumentListItemDto
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string? EmployeeNumber { get; set; }
    public string? JobTitle { get; set; }
    public string? Department { get; set; }
    public string? ProfilePictureUrl { get; set; }
    public string Category { get; set; } = HrDocumentCategories.Other;
    public string Name { get; set; } = string.Empty;

    /// <summary>Stored status (Active, Expired, Archived, Pending).</summary>
    public string Status { get; set; } = "Active";

    /// <summary>Computed status for the UI: Active, Expiring, Expired or Pending.</summary>
    public string DisplayStatus { get; set; } = HrDocumentDisplayStatuses.Active;

    public DateTime? ExpiresAt { get; set; }
    public Guid? DocumentId { get; set; }
    public string? ExternalUrl { get; set; }
    public long? FileSizeBytes { get; set; }
    public string? ContentType { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedByName { get; set; }
}

public class HrDocumentActivityDto
{
    public Guid Id { get; set; }
    public string ActivityType { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedByName { get; set; }
}

public class HrDocumentDetailDto : HrDocumentListItemDto
{
    public DateTimeOffset? UpdatedAt { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }

    /// <summary>Document activity for this record (only entries written by the hub carry the document id).</summary>
    public IReadOnlyList<HrDocumentActivityDto> Activity { get; set; } = Array.Empty<HrDocumentActivityDto>();
}

public class HrDocumentCategoryCountDto
{
    public string Category { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class HrDocumentSummaryDto
{
    public int TotalDocuments { get; set; }

    /// <summary>Documents that are valid: not pending, not expired and not expiring within 30 days.</summary>
    public int CompletedOrValid { get; set; }

    public int ExpiringIn30Days { get; set; }
    public int Expired { get; set; }
    public int Pending { get; set; }
    public IReadOnlyList<HrDocumentCategoryCountDto> ByCategory { get; set; } = Array.Empty<HrDocumentCategoryCountDto>();
    public int OpenRequests { get; set; }
}

public class HrDocumentListQuery
{
    public string? Search { get; set; }
    public string? Category { get; set; }

    /// <summary>Display status filter: Active, Expiring, Expired or Pending.</summary>
    public string? Status { get; set; }

    public Guid? DepartmentId { get; set; }
    public Guid? EmployeeId { get; set; }

    /// <summary>Only documents whose expiry falls between today and today + N days (inclusive).</summary>
    public int? ExpiringWithinDays { get; set; }

    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;

    /// <summary>name, category, employee, status, expiresAt, createdAt, size.</summary>
    public string Sort { get; set; } = "createdAt";

    public string Dir { get; set; } = "desc";
}

/// <summary>Editable document metadata (PUT body; also the base for link-only create).</summary>
public class UpsertHrDocumentRequest
{
    public string? Category { get; set; }
    public string? Name { get; set; }
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Active, Pending or Expired. Defaults to Active on create / unchanged on update.</summary>
    public string? Status { get; set; }

    /// <summary>http(s) link. Create requires this or <see cref="CreateHrDocumentRequest.DocumentId"/>.</summary>
    public string? ExternalUrl { get; set; }
}

public class CreateHrDocumentRequest : UpsertHrDocumentRequest
{
    public Guid EmployeeId { get; set; }

    /// <summary>Id of a blob already uploaded to the Documents service (optional when externalUrl is given).</summary>
    public Guid? DocumentId { get; set; }
}

/// <summary>multipart/form-data body for POST /api/hr/documents/upload.</summary>
public class UploadHrDocumentForm
{
    public Guid EmployeeId { get; set; }
    public string? Category { get; set; }
    public string? Name { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? Status { get; set; }
    public IFormFile? File { get; set; }
}

public class DocumentRequestDto
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string? ProfilePictureUrl { get; set; }
    public string Category { get; set; } = HrDocumentCategories.Other;
    public string Title { get; set; } = string.Empty;

    /// <summary>Open, Fulfilled, Cancelled or Overdue (Open requests past due are reported as Overdue).</summary>
    public string Status { get; set; } = HrDocumentRequestStatuses.Open;

    public DateTime? DueAt { get; set; }
    public string? Notes { get; set; }
    public Guid? FulfilledDocumentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public class CreateDocumentRequest
{
    public Guid EmployeeId { get; set; }
    public string? Category { get; set; }
    public string? Title { get; set; }
    public DateTime? DueAt { get; set; }
    public string? Notes { get; set; }
}

public class FulfillDocumentRequestRequest
{
    /// <summary>Optional hub document row (nh_hr_employee_documents.id) that satisfies the request; must belong to the same employee.</summary>
    public Guid? DocumentRowId { get; set; }
}
