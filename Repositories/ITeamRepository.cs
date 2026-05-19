using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public interface ITeamRepository
{
    Task<IReadOnlyList<TeamDto>> ListTeamsAsync(Guid orgId);
    Task<TeamDto?> GetTeamByIdAsync(Guid orgId, Guid teamId);
    Task<bool> TeamNameExistsAsync(Guid orgId, string nameTrimmed, Guid? excludeTeamId);
    Task<TeamDto> InsertTeamAsync(Guid orgId, CreateTeamRequestDto dto);
    Task<TeamDto?> UpdateTeamAsync(Guid orgId, Guid teamId, UpdateTeamRequestDto dto);
    Task<bool> DeleteTeamAsync(Guid orgId, Guid teamId);
    Task<bool> NhUserExistsInOrgAsync(Guid orgId, Guid nhUserId);
    Task<IReadOnlyList<TeamMemberDto>> ListMembersAsync(Guid orgId, Guid teamId);
    Task<bool> TeamMembershipExistsAsync(Guid orgId, Guid teamId, Guid userId);
    Task<TeamMemberDto?> InsertMemberAsync(Guid orgId, Guid teamId, Guid userId, bool isTeamLead);
    Task<bool> DeleteMemberAsync(Guid orgId, Guid teamId, Guid userId);
}
