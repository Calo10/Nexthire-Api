using System.Data;
using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public interface ICandidateRepository
{
    Task<PagedResult<CandidateListItemDto>> GetPagedAsync(
        Guid orgId,
        string? search,
        string? source,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int page,
        int pageSize,
        string sort,
        string dir);

    Task<CandidateDto?> GetByIdAsync(Guid orgId, Guid id);
    Task<CandidateDto?> GetByEmailAsync(Guid orgId, string emailLower);

    /// <summary>
    /// Match candidate phone by comparing digit-only normalization (org-scoped).
    /// </summary>
    Task<CandidateDto?> GetByPhoneDigitsAsync(Guid orgId, string phoneDigits);
    Task<Guid?> GetOrgIdByCandidateIdAsync(Guid candidateId);

    Task<bool> ExistsByEmailAsync(Guid orgId, string emailLower, Guid? excludeId = null);

    Task<CandidateDto> InsertAsync(Guid orgId, CreateCandidateRequestDto dto, string emailLower, IDbTransaction? transaction = null);

    Task<CandidateDto?> UpdateAsync(Guid orgId, Guid id, UpdateCandidateRequestDto dto, string emailLower);

    Task<bool> HasApplicationsAsync(Guid orgId, Guid candidateId);

    Task<bool> DeleteAsync(Guid orgId, Guid id);

    Task EnsureTagsSchemaAsync();

    Task<IReadOnlyList<CandidateTagDto>> ListTagsAsync(Guid orgId);

    Task<(CandidateTagDto? Tag, bool CandidateNotFound, string? Error)> AddTagAsync(Guid orgId, Guid candidateId, string name, string normalizedName);

    Task<bool> RemoveTagAsync(Guid orgId, Guid candidateId, Guid tagId);

    Task EnsureNotesSchemaAsync();

    Task<IReadOnlyList<CandidateNoteDto>> ListNotesAsync(Guid orgId, Guid candidateId);

    Task<(CandidateNoteDto? Note, bool CandidateNotFound)> AddNoteAsync(
        Guid orgId,
        Guid candidateId,
        string body,
        string? createdByName,
        string? createdByEmail);

    Task<bool> DeleteNoteAsync(Guid orgId, Guid candidateId, Guid noteId);
}

