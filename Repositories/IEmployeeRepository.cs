using nexthire_api.DTOs;
using nexthire_api.DTOs.CoreHr;

namespace nexthire_api.Repositories;

public interface IEmployeeRepository
{
    Task EnsureSchemaAsync();
    Task EnsureOrgDefaultsAsync(Guid organizationId, CancellationToken cancellationToken = default);
    Task SeedDemoEmployeesIfEmptyAsync(Guid organizationId, Guid? actorUserId, bool isDevelopment, CancellationToken cancellationToken = default);

    Task<PagedResult<EmployeeListItemDto>> ListAsync(Guid organizationId, EmployeeListQuery query, CancellationToken cancellationToken = default);
    Task<EmployeeDetailDto?> GetByIdAsync(Guid organizationId, Guid employeeId, CancellationToken cancellationToken = default);
    Task<Guid?> FindActiveByCandidateIdAsync(Guid organizationId, Guid candidateId, CancellationToken cancellationToken = default);
    Task<bool> EmployeeNumberExistsAsync(Guid organizationId, string employeeNumber, Guid? excludeId, CancellationToken cancellationToken = default);
    Task<string> NextEmployeeNumberAsync(Guid organizationId, CancellationToken cancellationToken = default);
    Task<bool> WouldCreateManagerCycleAsync(Guid organizationId, Guid employeeId, Guid managerId, CancellationToken cancellationToken = default);

    Task<Guid> InsertAsync(Guid organizationId, Guid employeeId, CreateEmployeeRequest request, string employeeNumber, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<bool> UpdateAsync(Guid organizationId, Guid employeeId, UpdateEmployeeRequest request, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<bool> ChangeStatusAsync(Guid organizationId, Guid employeeId, ChangeEmployeeStatusRequest request, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<bool> ArchiveAsync(Guid organizationId, Guid employeeId, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<bool> RestoreAsync(Guid organizationId, Guid employeeId, Guid? actorUserId, CancellationToken cancellationToken = default);

    Task ReplaceEmergencyContactsAsync(Guid organizationId, Guid employeeId, IReadOnlyList<EmergencyContactDto> contacts, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EmergencyContactDto>> ListEmergencyContactsAsync(Guid organizationId, Guid employeeId, CancellationToken cancellationToken = default);

    Task AddActivityAsync(Guid organizationId, Guid employeeId, string activityType, string summary, string? detailsJson, Guid? actorUserId, string? actorName, CancellationToken cancellationToken = default);
    Task AddEmploymentHistoryAsync(Guid organizationId, Guid employeeId, string changeType, CreateEmployeeRequest snapshot, Guid? actorUserId, string? notes, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EmployeeActivityDto>> ListActivityAsync(Guid organizationId, Guid employeeId, int take, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EmploymentHistoryDto>> ListHistoryAsync(Guid organizationId, Guid employeeId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EmployeeDocumentDto>> ListDocumentsAsync(Guid organizationId, Guid employeeId, bool includeArchived, CancellationToken cancellationToken = default);
    Task<EmployeeDocumentDto?> GetDocumentAsync(Guid organizationId, Guid documentRowId, CancellationToken cancellationToken = default);
    Task<Guid> InsertDocumentAsync(Guid organizationId, Guid employeeId, UpsertEmployeeDocumentRequest request, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<bool> ArchiveDocumentAsync(Guid organizationId, Guid documentRowId, Guid? actorUserId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CompensationDto>> ListCompensationAsync(Guid organizationId, Guid employeeId, CancellationToken cancellationToken = default);
    Task<Guid> InsertCompensationAsync(Guid organizationId, Guid employeeId, UpsertCompensationRequest request, Guid? actorUserId, CancellationToken cancellationToken = default);

    Task<EmployeeSummaryDto> GetSummaryAsync(Guid organizationId, DateTime? from, DateTime? to, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HrLookupDto>> ListDepartmentsAsync(Guid organizationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HrLookupDto>> ListJobTitlesAsync(Guid organizationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkLocationDto>> ListLocationsAsync(Guid organizationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HrLookupDto>> ListEmploymentTypesAsync(Guid organizationId, CancellationToken cancellationToken = default);
    Task<Guid> UpsertDepartmentAsync(Guid organizationId, CreateHrLookupRequest request, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<Guid> UpsertJobTitleAsync(Guid organizationId, CreateHrLookupRequest request, Guid? actorUserId, CancellationToken cancellationToken = default);
    Task<Guid> UpsertLocationAsync(Guid organizationId, CreateHrLookupRequest request, Guid? actorUserId, CancellationToken cancellationToken = default);

    Task<(string? FirstName, string? LastName, string? Email, string? Phone, string? ResumeUrl)?> GetCandidateBasicsAsync(Guid organizationId, Guid candidateId, CancellationToken cancellationToken = default);
    Task<(Guid ApplicationId, Guid CandidateId, string? StageName, string? Status)?> GetApplicationHireInfoAsync(Guid organizationId, Guid applicationId, CancellationToken cancellationToken = default);
    Task<(Guid ApplicationId, string? StageName, string? Status)?> FindHiredApplicationForCandidateAsync(Guid organizationId, Guid candidateId, CancellationToken cancellationToken = default);
}
