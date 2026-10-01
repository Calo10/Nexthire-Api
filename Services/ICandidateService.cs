using nexthire_api.DTOs;

namespace nexthire_api.Services;

public interface ICandidateService
{
    Task<PagedResult<CandidateListItemDto>> GetPagedAsync(Guid orgId, string? search, string? source, DateTimeOffset? from, DateTimeOffset? to, int page, int pageSize, string? sort, string? dir);
    Task<CandidateDto?> GetByIdAsync(Guid orgId, Guid id);
    Task<(CandidateDto? Candidate, bool EmailConflict)> CreateAsync(Guid orgId, Guid userId, CreateCandidateRequestDto dto);
    Task<(CandidateDto? Candidate, bool NotFound, bool EmailConflict)> UpdateAsync(Guid orgId, Guid userId, Guid id, UpdateCandidateRequestDto dto);
    Task<(bool Deleted, bool NotFound, bool HasApplications)> DeleteAsync(Guid orgId, Guid userId, Guid id);
    Task<IReadOnlyList<CandidateTagDto>> ListTagsAsync(Guid orgId);
    Task<(CandidateTagDto? Tag, bool NotFound, string? Error)> AddTagAsync(Guid orgId, Guid candidateId, string name);
    Task<bool> RemoveTagAsync(Guid orgId, Guid candidateId, Guid tagId);
    Task<(IReadOnlyList<CandidateNoteDto>? Notes, bool NotFound)> ListNotesAsync(Guid orgId, Guid candidateId);
    Task<(CandidateNoteDto? Note, bool NotFound, string? Error)> AddNoteAsync(Guid orgId, Guid candidateId, string body, string? createdByName, string? createdByEmail);
    Task<bool> DeleteNoteAsync(Guid orgId, Guid candidateId, Guid noteId);
}

