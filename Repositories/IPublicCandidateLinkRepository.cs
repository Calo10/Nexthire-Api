using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public interface IPublicCandidateLinkRepository
{
    Task<Guid> InsertAsync(
        Guid orgId,
        Guid? createdBy,
        string name,
        string? search,
        string? source,
        DateTimeOffset? from,
        DateTimeOffset? to,
        IReadOnlyCollection<Guid> tagIds);

    Task<PublicCandidateLinkRecord?> GetByIdAsync(Guid id);

    Task<IReadOnlyList<PublicCandidateLinkAdminDto>> ListByOrgAsync(Guid orgId);

    Task<bool> DeleteAsync(Guid orgId, Guid id);
}
