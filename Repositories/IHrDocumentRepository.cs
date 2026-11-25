using nexthire_api.DTOs;
using nexthire_api.DTOs.CoreHr;

namespace nexthire_api.Repositories;

/// <summary>Values for a new row in nh_hr_employee_documents (already validated and normalized by the service).</summary>
public sealed record HrDocumentInsert(
    Guid EmployeeId,
    string Category,
    string Name,
    Guid? DocumentId,
    string? ExternalUrl,
    string Status,
    DateTime? ExpiresAt,
    string? ContentType,
    long? FileSizeBytes);

/// <summary>
/// Org-wide Core HR documents library. <c>nh_hr_employee_documents</c> is the source of truth (joined to employees);
/// all queries are parameterized and scoped by organization_id. Archived documents (and documents of archived
/// employees) are never returned.
/// </summary>
public interface IHrDocumentRepository
{
    /// <summary>Runs sql/012-core-hr-documents-hub.sql (must run after the employees schema).</summary>
    Task EnsureSchemaAsync();

    Task<PagedResult<HrDocumentListItemDto>> ListAsync(Guid organizationId, HrDocumentListQuery query, DateTime today, CancellationToken cancellationToken = default);
    Task<HrDocumentDetailDto?> GetByIdAsync(Guid organizationId, Guid id, DateTime today, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HrDocumentActivityDto>> GetActivityAsync(Guid organizationId, Guid employeeId, Guid documentRowId, int take, CancellationToken cancellationToken = default);

    Task<HrDocumentSummaryDto> SummaryAsync(Guid organizationId, DateTime today, CancellationToken cancellationToken = default);

    /// <summary>Counts per canonical category (all nine categories are always returned, in canonical order).</summary>
    Task<IReadOnlyList<HrDocumentCategoryCountDto>> CategoryCountsAsync(Guid organizationId, CancellationToken cancellationToken = default);

    Task<Guid> InsertAsync(Guid organizationId, HrDocumentInsert row, Guid? actorUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates metadata. <paramref name="status"/> and <paramref name="externalUrl"/> are left unchanged when null;
    /// <paramref name="expiresAt"/> is always written (null clears it).
    /// </summary>
    Task<bool> UpdateAsync(
        Guid organizationId,
        Guid id,
        string category,
        string name,
        string? status,
        DateTime? expiresAt,
        string? externalUrl,
        Guid? actorUserId,
        CancellationToken cancellationToken = default);

    Task<bool> ArchiveAsync(Guid organizationId, Guid id, Guid? actorUserId, CancellationToken cancellationToken = default);

    /// <summary>Development only: when the org has employees but fewer than 3 documents, attaches demo documents.</summary>
    Task SeedDemoDocumentsIfEmptyAsync(Guid organizationId, Guid? actorUserId, bool isDevelopment, CancellationToken cancellationToken = default);

    // ---- Document requests ----

    /// <summary>Lists requests; <c>statuses</c> are stored statuses to include (null returns every status).</summary>
    Task<IReadOnlyList<DocumentRequestDto>> ListRequestsAsync(Guid organizationId, IReadOnlyCollection<string>? statuses, Guid? employeeId, DateTime today, CancellationToken cancellationToken = default);
    Task<DocumentRequestDto?> GetRequestAsync(Guid organizationId, Guid id, DateTime today, CancellationToken cancellationToken = default);
    Task<Guid> CreateRequestAsync(Guid organizationId, Guid employeeId, string category, string title, DateTime? dueAt, string? notes, Guid? actorUserId, CancellationToken cancellationToken = default);

    /// <summary>Marks an Open/Overdue request Fulfilled. Returns false when it is missing or already closed.</summary>
    Task<bool> FulfillRequestAsync(Guid organizationId, Guid id, Guid? fulfilledDocumentRowId, CancellationToken cancellationToken = default);

    /// <summary>Marks an Open/Overdue request Cancelled. Returns false when it is missing or already closed.</summary>
    Task<bool> CancelRequestAsync(Guid organizationId, Guid id, CancellationToken cancellationToken = default);

    Task<int> CountOpenRequestsAsync(Guid organizationId, CancellationToken cancellationToken = default);
}
