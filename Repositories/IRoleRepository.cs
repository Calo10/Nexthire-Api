using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public interface IRoleRepository
{
    Task EnsureDefaultRolesAsync(Guid orgId);
    Task<IReadOnlyList<RoleDto>> ListRolesAsync(Guid orgId);
    Task<RoleDto?> GetRoleByIdAsync(Guid orgId, Guid roleId);
    Task<bool> NhUserExistsInOrgAsync(Guid orgId, Guid nhUserId);
    Task<IReadOnlyList<UserRoleDto>> ListUserRolesAsync(Guid orgId, Guid userId);
    Task<bool> UserRoleExistsAsync(Guid orgId, Guid userId, Guid roleId);
    Task<UserRoleDto?> InsertUserRoleAsync(Guid orgId, Guid userId, Guid roleId);
    Task<bool> DeleteUserRoleAsync(Guid orgId, Guid userId, Guid roleId);
}
