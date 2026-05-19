using nexthire_api.DTOs;

namespace nexthire_api.Services;

public interface ITeamService
{
    Task<IReadOnlyList<TeamDto>> GetTeamsAsync(Guid orgId);
    Task<TeamDto?> GetTeamByIdAsync(Guid orgId, Guid teamId);
    Task<(TeamDto? Team, bool NameConflict)> CreateAsync(Guid orgId, CreateTeamRequestDto dto);
    Task<(TeamDto? Team, bool NotFound, bool NameConflict)> UpdateAsync(Guid orgId, Guid teamId, UpdateTeamRequestDto dto);
    Task<(bool Deleted, bool NotFound)> DeleteAsync(Guid orgId, Guid teamId);
    Task<IReadOnlyList<TeamMemberDto>> GetMembersAsync(Guid orgId, Guid teamId);
    Task<(TeamMemberDto? Member, bool TeamNotFound, bool UserNotFound, bool Duplicate)> AddMemberAsync(
        Guid orgId,
        Guid teamId,
        AddTeamMemberRequestDto dto);
    Task<(bool Removed, bool TeamNotFound, bool UserNotFound, bool NotMember)> RemoveMemberAsync(
        Guid orgId,
        Guid teamId,
        Guid userId);
}
