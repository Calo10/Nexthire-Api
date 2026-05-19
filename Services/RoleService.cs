using nexthire_api.DTOs;
using nexthire_api.Repositories;

namespace nexthire_api.Services;

public class RoleService : IRoleService
{
    private readonly IRoleRepository _repo;
    private readonly ILogger<RoleService> _logger;

    public RoleService(IRoleRepository repo, ILogger<RoleService> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RoleDto>> GetRolesAsync(Guid orgId)
    {
        await _repo.EnsureDefaultRolesAsync(orgId);
        return await _repo.ListRolesAsync(orgId);
    }

    public async Task<(IReadOnlyList<UserRoleDto>? Roles, bool UserNotFound)> GetUserRolesAsync(Guid orgId, Guid userId)
    {
        if (!await _repo.NhUserExistsInOrgAsync(orgId, userId))
            return (null, true);

        var rows = await _repo.ListUserRolesAsync(orgId, userId);
        return (rows, false);
    }

    public async Task<(UserRoleDto? Assignment, bool UserNotFound, bool RoleNotFound, bool Duplicate)> AssignRoleAsync(
        Guid orgId,
        Guid userId,
        AssignUserRoleRequestDto dto)
    {
        await _repo.EnsureDefaultRolesAsync(orgId);

        if (!await _repo.NhUserExistsInOrgAsync(orgId, userId))
        {
            _logger.LogInformation("Assign role skipped: user not in org. OrgId: {OrgId}, UserId: {UserId}", orgId, userId);
            return (null, true, false, false);
        }

        var role = await _repo.GetRoleByIdAsync(orgId, dto.RoleId);
        if (role == null)
            return (null, false, true, false);

        if (await _repo.UserRoleExistsAsync(orgId, userId, dto.RoleId))
        {
            _logger.LogInformation("Assign role conflict: already assigned. OrgId: {OrgId}, UserId: {UserId}, RoleId: {RoleId}",
                orgId, userId, dto.RoleId);
            return (null, false, false, true);
        }

        var created = await _repo.InsertUserRoleAsync(orgId, userId, dto.RoleId);
        return (created, false, false, false);
    }

    public async Task<(bool Removed, bool NotFound, bool UserNotFound, bool RoleNotFound)> RemoveRoleAsync(
        Guid orgId,
        Guid userId,
        Guid roleId)
    {
        if (!await _repo.NhUserExistsInOrgAsync(orgId, userId))
            return (false, false, true, false);

        if (await _repo.GetRoleByIdAsync(orgId, roleId) == null)
            return (false, false, false, true);

        if (!await _repo.UserRoleExistsAsync(orgId, userId, roleId))
            return (false, true, false, false);

        var removed = await _repo.DeleteUserRoleAsync(orgId, userId, roleId);
        return (removed, !removed, false, false);
    }
}
