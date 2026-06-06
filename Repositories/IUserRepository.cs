using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public interface IUserRepository
{
    Task EnsureSchemaAsync();
    Task<IReadOnlyList<OrgUserDto>> ListByOrgAsync(Guid orgId);
    Task<OrgUserDto?> GetByIdAsync(Guid orgId, Guid userId);
    Task<OrgUserDto?> GetByEmailAsync(Guid orgId, string email);
    Task<Guid> UpsertPendingByEmailAsync(Guid orgId, string email, string? firstName, string? lastName, string? phone);
    Task<Guid> UpsertFromNexaMemberAsync(Guid orgId, Guid nexaUserId, string email, string? fullName);
    Task<OrgUserDto?> UpdateProfileAsync(Guid orgId, Guid userId, UpdateOrgUserRequestDto dto);
    Task<bool> DeleteFromOrgAsync(Guid orgId, Guid userId);
}
