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

    public Task<PagedResult<CandidateListItemDto>> GetPagedAsync(Guid orgId, string? search, string? source, DateTimeOffset? from, DateTimeOffset? to, int page, int pageSize, string? sort, string? dir)
    {
        var sortValue = string.IsNullOrWhiteSpace(sort) ? "created_at" : sort!;
        var dirValue = string.IsNullOrWhiteSpace(dir) ? "desc" : dir!;
        return _repo.GetPagedAsync(orgId, search, source, from, to, page, pageSize, sortValue, dirValue);
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

    private static string NormalizeEmail(string email)
    {
        return (email ?? string.Empty).Trim().ToLowerInvariant();
    }
}

