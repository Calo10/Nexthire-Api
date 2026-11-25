using nexthire_api.DTOs;
using nexthire_api.DTOs.CoreHr;

namespace nexthire_api.Services;

/// <summary>The authenticated caller performing a Core HR operation.</summary>
/// <param name="OrganizationId">Tenant (organization_id).</param>
/// <param name="UserId">Nexa user id (nexa_user_id claim); used for role resolution and audit columns.</param>
/// <param name="Name">Optional display name recorded on activity entries.</param>
public sealed record HrActor(Guid OrganizationId, Guid UserId, string? Name = null);

/// <summary>
/// Core HR employee operations. Every method resolves the caller's role codes and enforces
/// <see cref="nexthire_api.Security.HrPermissions"/>; a missing capability throws
/// <see cref="nexthire_api.Exceptions.HrAccessDeniedException"/> (an <see cref="UnauthorizedAccessException"/>).
/// Business-rule failures throw <see cref="nexthire_api.Exceptions.EmployeeServiceException"/>.
/// </summary>
public interface IEmployeeService
{
    Task<IReadOnlyList<string>> GetCapabilitiesAsync(HrActor actor, CancellationToken cancellationToken = default);

    Task<PagedResult<EmployeeListItemDto>> ListAsync(HrActor actor, EmployeeListQuery query, CancellationToken cancellationToken = default);
    Task<EmployeeDetailDto?> GetAsync(HrActor actor, Guid employeeId, CancellationToken cancellationToken = default);
    Task<EmployeeDetailDto> CreateAsync(HrActor actor, CreateEmployeeRequest request, CancellationToken cancellationToken = default);
    Task<EmployeeDetailDto?> UpdateAsync(HrActor actor, Guid employeeId, UpdateEmployeeRequest request, CancellationToken cancellationToken = default);

    /// <summary>Soft-archives the employee (archived_at / archived_by). Returns false when not found / already archived.</summary>
    Task<bool> DeleteAsync(HrActor actor, Guid employeeId, CancellationToken cancellationToken = default);
    Task<EmployeeDetailDto?> RestoreAsync(HrActor actor, Guid employeeId, CancellationToken cancellationToken = default);
    Task<EmployeeDetailDto?> ChangeStatusAsync(HrActor actor, Guid employeeId, ChangeEmployeeStatusRequest request, CancellationToken cancellationToken = default);

    Task<EmployeeSummaryDto> GetSummaryAsync(HrActor actor, DateTime? from, DateTime? to, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EmployeeActivityDto>> GetActivityAsync(HrActor actor, Guid employeeId, int take, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EmploymentHistoryDto>> GetHistoryAsync(HrActor actor, Guid employeeId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EmployeeDocumentDto>> ListDocumentsAsync(HrActor actor, Guid employeeId, bool includeArchived, CancellationToken cancellationToken = default);
    Task<EmployeeDocumentDto> AddDocumentAsync(HrActor actor, Guid employeeId, UpsertEmployeeDocumentRequest request, CancellationToken cancellationToken = default);
    Task<bool> ArchiveDocumentAsync(HrActor actor, Guid employeeId, Guid documentRowId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CompensationDto>> ListCompensationAsync(HrActor actor, Guid employeeId, CancellationToken cancellationToken = default);
    Task<CompensationDto> AddCompensationAsync(HrActor actor, Guid employeeId, UpsertCompensationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Imports employees from CSV text (header row required).</summary>
    Task<EmployeeImportResultDto> ImportAsync(HrActor actor, string csv, CancellationToken cancellationToken = default);

    /// <summary>Exports the current filter results as CSV text (capped at 5000 rows).</summary>
    Task<string> ExportAsync(HrActor actor, EmployeeListQuery query, CancellationToken cancellationToken = default);

    /// <summary>Idempotent: returns the existing active employee for the candidate, otherwise creates one from a hired application.</summary>
    Task<EmployeeDetailDto> ConvertFromCandidateAsync(HrActor actor, ConvertCandidateToEmployeeRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HrLookupDto>> ListDepartmentsAsync(HrActor actor, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HrLookupDto>> ListJobTitlesAsync(HrActor actor, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkLocationDto>> ListLocationsAsync(HrActor actor, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HrLookupDto>> ListEmploymentTypesAsync(HrActor actor, CancellationToken cancellationToken = default);
    Task<HrLookupDto> UpsertDepartmentAsync(HrActor actor, CreateHrLookupRequest request, CancellationToken cancellationToken = default);
    Task<HrLookupDto> UpsertJobTitleAsync(HrActor actor, CreateHrLookupRequest request, CancellationToken cancellationToken = default);
    Task<WorkLocationDto> UpsertLocationAsync(HrActor actor, CreateHrLookupRequest request, CancellationToken cancellationToken = default);
}
