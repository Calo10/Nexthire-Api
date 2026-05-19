using nexthire_api.DTOs;

namespace nexthire_api.Services;

public interface ICandidateService
{
    Task<PagedResult<CandidateListItemDto>> GetPagedAsync(Guid orgId, string? search, string? source, DateTimeOffset? from, DateTimeOffset? to, int page, int pageSize, string? sort, string? dir);
    Task<CandidateDto?> GetByIdAsync(Guid orgId, Guid id);
    Task<(CandidateDto? Candidate, bool EmailConflict)> CreateAsync(Guid orgId, Guid userId, CreateCandidateRequestDto dto);
    Task<(CandidateDto? Candidate, bool NotFound, bool EmailConflict)> UpdateAsync(Guid orgId, Guid userId, Guid id, UpdateCandidateRequestDto dto);
    Task<(bool Deleted, bool NotFound, bool HasApplications)> DeleteAsync(Guid orgId, Guid userId, Guid id);
}

