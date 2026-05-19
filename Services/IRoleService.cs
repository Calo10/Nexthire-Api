using nexthire_api.DTOs;

namespace nexthire_api.Services;

public interface IRoleService
{
    Task<IReadOnlyList<RoleDto>> GetRolesAsync(Guid orgId);
    Task<(IReadOnlyList<UserRoleDto>? Roles, bool UserNotFound)> GetUserRolesAsync(Guid orgId, Guid userId);
    Task<(UserRoleDto? Assignment, bool UserNotFound, bool RoleNotFound, bool Duplicate)> AssignRoleAsync(
        Guid orgId,
        Guid userId,
        AssignUserRoleRequestDto dto);
    Task<(bool Removed, bool NotFound, bool UserNotFound, bool RoleNotFound)> RemoveRoleAsync(
        Guid orgId,
        Guid userId,
        Guid roleId);
}
