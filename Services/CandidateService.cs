using nexthire_api.DTOs;
using nexthire_api.Repositories;

namespace nexthire_api.Services;

public class CandidateService : ICandidateService
{
    private readonly ICandidateRepository _repo;
    private readonly ILogger<CandidateService> _logger;

    public CandidateService(ICandidateRepository repo, ILogger<CandidateService> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public Task<PagedResult<CandidateListItemDto>> GetPagedAsync(Guid orgId, string? search, string? source, DateTimeOffset? from, DateTimeOffset? to, IReadOnlyCollection<Guid>? tagIds, int page, int pageSize, string? sort, string? dir)
    {
        var sortValue = string.IsNullOrWhiteSpace(sort) ? "created_at" : sort!;
        var dirValue = string.IsNullOrWhiteSpace(dir) ? "desc" : dir!;
        return _repo.GetPagedAsync(orgId, search, source, from, to, tagIds, page, pageSize, sortValue, dirValue);
    }

    public Task<CandidateDto?> GetByIdAsync(Guid orgId, Guid id)
    {
        return _repo.GetByIdAsync(orgId, id);
    }

    public async Task<(CandidateDto? Candidate, bool EmailConflict)> CreateAsync(Guid orgId, Guid userId, CreateCandidateRequestDto dto)
    {
        var emailLower = NormalizeEmail(dto.Email);

        var exists = await _repo.ExistsByEmailAsync(orgId, emailLower);
        if (exists)
        {
            _logger.LogInformation("Candidate email conflict on create. OrgId: {OrgId}, Email: {Email}", orgId, emailLower);
            return (null, true);
        }

        var created = await _repo.InsertAsync(orgId, dto, emailLower);
        return (created, false);
    }

    public async Task<(CandidateDto? Candidate, bool NotFound, bool EmailConflict)> UpdateAsync(Guid orgId, Guid userId, Guid id, UpdateCandidateRequestDto dto)
    {
        var existing = await _repo.GetByIdAsync(orgId, id);
        if (existing == null)
            return (null, true, false);

        var emailLower = NormalizeEmail(dto.Email);

        var exists = await _repo.ExistsByEmailAsync(orgId, emailLower, excludeId: id);
        if (exists)
        {
            _logger.LogInformation("Candidate email conflict on update. OrgId: {OrgId}, CandidateId: {CandidateId}, Email: {Email}", orgId, id, emailLower);
            return (null, false, true);
        }

        var updated = await _repo.UpdateAsync(orgId, id, dto, emailLower);
        return (updated, false, false);
    }

    public async Task<(bool Deleted, bool NotFound, bool HasApplications)> DeleteAsync(Guid orgId, Guid userId, Guid id)
    {
        var existing = await _repo.GetByIdAsync(orgId, id);
        if (existing == null)
            return (false, true, false);

        var hasApps = await _repo.HasApplicationsAsync(orgId, id);
        if (hasApps)
            return (false, false, true);

        var deleted = await _repo.DeleteAsync(orgId, id);
        return (deleted, !deleted, false);
    }

    public Task<IReadOnlyList<CandidateTagDto>> ListTagsAsync(Guid orgId)
    {
        return _repo.ListTagsAsync(orgId);
    }

    public Task<(CandidateTagDto? Tag, bool NotFound, string? Error)> AddTagAsync(Guid orgId, Guid candidateId, string name)
    {
        var normalized = NormalizeTagName(name);
        if (normalized == null)
            return Task.FromResult<(CandidateTagDto?, bool, string?)>((null, false, "Tag name is required."));
        if (normalized.Length > 40)
            return Task.FromResult<(CandidateTagDto?, bool, string?)>((null, false, "Tag name must be 40 characters or fewer."));

        return _repo.AddTagAsync(orgId, candidateId, normalized, normalized.ToLowerInvariant());
    }

    public Task<bool> RemoveTagAsync(Guid orgId, Guid candidateId, Guid tagId)
    {
        return _repo.RemoveTagAsync(orgId, candidateId, tagId);
    }

    public async Task<(IReadOnlyList<CandidateNoteDto>? Notes, bool NotFound)> ListNotesAsync(Guid orgId, Guid candidateId)
    {
        var existing = await _repo.GetByIdAsync(orgId, candidateId);
        if (existing == null)
            return (null, true);

        var notes = await _repo.ListNotesAsync(orgId, candidateId);
        return (notes, false);
    }

    public async Task<(CandidateNoteDto? Note, bool NotFound, string? Error)> AddNoteAsync(
        Guid orgId,
        Guid candidateId,
        string body,
        string? createdByName,
        string? createdByEmail)
    {
        var trimmed = (body ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return (null, false, "Note is required.");
        if (trimmed.Length > 4000)
            return (null, false, "Note must be 4000 characters or fewer.");

        var (note, notFound) = await _repo.AddNoteAsync(orgId, candidateId, trimmed, createdByName, createdByEmail);
        return (note, notFound, null);
    }

    public Task<bool> DeleteNoteAsync(Guid orgId, Guid candidateId, Guid noteId)
    {
        return _repo.DeleteNoteAsync(orgId, candidateId, noteId);
    }

    private static string? NormalizeTagName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var parts = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? null : string.Join(' ', parts);
    }

    private static string NormalizeEmail(string email)
    {
        return (email ?? string.Empty).Trim().ToLowerInvariant();
    }
}

