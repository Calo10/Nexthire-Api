using nexthire_api.DTOs;
using nexthire_api.DTOs.CoreHr;

namespace nexthire_api.Services;

/// <summary>
/// Core HR Documents hub operations. Reads require <see cref="nexthire_api.Security.HrPermissions.View"/>;
/// writes and uploads require <see cref="nexthire_api.Security.HrPermissions.ManageDocuments"/>
/// (<see cref="nexthire_api.Exceptions.HrAccessDeniedException"/> otherwise). Business-rule failures throw
/// <see cref="nexthire_api.Exceptions.EmployeeServiceException"/>.
/// </summary>
public interface IHrDocumentService
{
    Task<IReadOnlyList<string>> GetCapabilitiesAsync(HrActor actor, CancellationToken cancellationToken = default);

    Task<PagedResult<HrDocumentListItemDto>> ListAsync(HrActor actor, HrDocumentListQuery query, CancellationToken cancellationToken = default);
    Task<HrDocumentDetailDto?> GetAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default);
    Task<HrDocumentSummaryDto> GetSummaryAsync(HrActor actor, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HrDocumentCategoryCountDto>> GetCategoriesAsync(HrActor actor, CancellationToken cancellationToken = default);

    /// <summary>Link-only (externalUrl) or pre-uploaded (documentId) document for an employee.</summary>
    Task<HrDocumentDetailDto> CreateAsync(HrActor actor, CreateHrDocumentRequest request, CancellationToken cancellationToken = default);

    /// <summary>Uploads the file via the Documents service and stores document_id, content_type and file_size_bytes.</summary>
    Task<HrDocumentDetailDto> UploadAsync(HrActor actor, UploadHrDocumentForm form, CancellationToken cancellationToken = default);

    Task<HrDocumentDetailDto?> UpdateAsync(HrActor actor, Guid id, UpsertHrDocumentRequest request, CancellationToken cancellationToken = default);

    /// <summary>Soft-archives the document. Returns false when not found / already archived.</summary>
    Task<bool> ArchiveAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default);

    /// <summary>Lists requests; <c>status</c> is "open" (default: Open + Overdue), "all", or one of Open, Fulfilled, Cancelled, Overdue.</summary>
    Task<IReadOnlyList<DocumentRequestDto>> ListRequestsAsync(HrActor actor, string? status, Guid? employeeId, CancellationToken cancellationToken = default);
    Task<DocumentRequestDto> CreateRequestAsync(HrActor actor, CreateDocumentRequest request, CancellationToken cancellationToken = default);
    Task<DocumentRequestDto?> FulfillRequestAsync(HrActor actor, Guid requestId, FulfillDocumentRequestRequest? request, CancellationToken cancellationToken = default);
    Task<DocumentRequestDto?> CancelRequestAsync(HrActor actor, Guid requestId, CancellationToken cancellationToken = default);
}
