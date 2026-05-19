using nexthire_api.DTOs;
using nexthire_api.Repositories;

namespace nexthire_api.Services;

public class TeamService : ITeamService
{
    private readonly ITeamRepository _repo;
    private readonly ILogger<TeamService> _logger;

    public TeamService(ITeamRepository repo, ILogger<TeamService> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public Task<IReadOnlyList<TeamDto>> GetTeamsAsync(Guid orgId)
    {
        return _repo.ListTeamsAsync(orgId);
    }

    public Task<TeamDto?> GetTeamByIdAsync(Guid orgId, Guid teamId)
    {
        return _repo.GetTeamByIdAsync(orgId, teamId);
    }

    public async Task<(TeamDto? Team, bool NameConflict)> CreateAsync(Guid orgId, CreateTeamRequestDto dto)
    {
        var name = dto.Name.Trim();
        if (await _repo.TeamNameExistsAsync(orgId, name, excludeTeamId: null))
        {
            _logger.LogInformation("Team name conflict on create. OrgId: {OrgId}, Name: {Name}", orgId, name);
            return (null, true);
        }

        var created = await _repo.InsertTeamAsync(orgId, dto);
        return (created, false);
    }

    public async Task<(TeamDto? Team, bool NotFound, bool NameConflict)> UpdateAsync(Guid orgId, Guid teamId, UpdateTeamRequestDto dto)
    {
        var existing = await _repo.GetTeamByIdAsync(orgId, teamId);
        if (existing == null)
            return (null, true, false);

        var name = dto.Name.Trim();
        if (await _repo.TeamNameExistsAsync(orgId, name, excludeTeamId: teamId))
        {
            _logger.LogInformation("Team name conflict on update. OrgId: {OrgId}, TeamId: {TeamId}, Name: {Name}", orgId, teamId, name);
            return (null, false, true);
        }

        var updated = await _repo.UpdateTeamAsync(orgId, teamId, dto);
        return (updated, false, false);
    }

    public async Task<(bool Deleted, bool NotFound)> DeleteAsync(Guid orgId, Guid teamId)
    {
        var existing = await _repo.GetTeamByIdAsync(orgId, teamId);
        if (existing == null)
            return (false, true);

        var deleted = await _repo.DeleteTeamAsync(orgId, teamId);
        return (deleted, !deleted);
    }

    public Task<IReadOnlyList<TeamMemberDto>> GetMembersAsync(Guid orgId, Guid teamId)
    {
        return _repo.ListMembersAsync(orgId, teamId);
    }

    public async Task<(TeamMemberDto? Member, bool TeamNotFound, bool UserNotFound, bool Duplicate)> AddMemberAsync(
        Guid orgId,
        Guid teamId,
        AddTeamMemberRequestDto dto)
    {
        if (await _repo.GetTeamByIdAsync(orgId, teamId) == null)
            return (null, true, false, false);

        if (!await _repo.NhUserExistsInOrgAsync(orgId, dto.UserId))
            return (null, false, true, false);

        if (await _repo.TeamMembershipExistsAsync(orgId, teamId, dto.UserId))
        {
            _logger.LogInformation("Team membership conflict. OrgId: {OrgId}, TeamId: {TeamId}, UserId: {UserId}", orgId, teamId, dto.UserId);
            return (null, false, false, true);
        }

        var member = await _repo.InsertMemberAsync(orgId, teamId, dto.UserId, dto.IsTeamLead);
        return (member, false, false, false);
    }

    public async Task<(bool Removed, bool TeamNotFound, bool UserNotFound, bool NotMember)> RemoveMemberAsync(
        Guid orgId,
        Guid teamId,
        Guid userId)
    {
        if (await _repo.GetTeamByIdAsync(orgId, teamId) == null)
            return (false, true, false, false);

        if (!await _repo.NhUserExistsInOrgAsync(orgId, userId))
            return (false, false, true, false);

        if (!await _repo.TeamMembershipExistsAsync(orgId, teamId, userId))
            return (false, false, false, true);

        var removed = await _repo.DeleteMemberAsync(orgId, teamId, userId);
        return (removed, false, false, false);
    }
}
