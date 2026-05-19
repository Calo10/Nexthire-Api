using Dapper;
using nexthire_api.Data;
using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public class TeamRepository : ITeamRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<TeamRepository> _logger;

    public TeamRepository(IDbConnectionFactory connectionFactory, ILogger<TeamRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TeamDto>> ListTeamsAsync(Guid orgId)
    {
        const string sql = @"
            SELECT
                t.id AS Id,
                t.name AS Name,
                t.description AS Description,
                t.created_at AS CreatedAt,
                t.updated_at AS UpdatedAt
            FROM teams t
            WHERE t.org_id = @orgId
            ORDER BY t.name ASC;";

        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<TeamDto>(sql, new { orgId });
        return rows.ToList();
    }

    public async Task<TeamDto?> GetTeamByIdAsync(Guid orgId, Guid teamId)
    {
        const string sql = @"
            SELECT
                t.id AS Id,
                t.name AS Name,
                t.description AS Description,
                t.created_at AS CreatedAt,
                t.updated_at AS UpdatedAt
            FROM teams t
            WHERE t.org_id = @orgId AND t.id = @teamId;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<TeamDto>(sql, new { orgId, teamId });
    }

    public async Task<bool> TeamNameExistsAsync(Guid orgId, string nameTrimmed, Guid? excludeTeamId)
    {
        const string sql = @"
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM teams t
                WHERE t.org_id = @orgId
                  AND LOWER(LTRIM(RTRIM(t.name))) = LOWER(@nameTrimmed)
                  AND (@excludeTeamId IS NULL OR t.id <> @excludeTeamId)
            ) THEN 1 ELSE 0 END;";

        using var connection = _connectionFactory.CreateConnection();
        var v = await connection.ExecuteScalarAsync<int>(sql, new { orgId, nameTrimmed, excludeTeamId });
        return v == 1;
    }

    public async Task<TeamDto> InsertTeamAsync(Guid orgId, CreateTeamRequestDto dto)
    {
        const string sql = @"
            INSERT INTO teams (id, org_id, name, description, created_at, updated_at)
            OUTPUT
                INSERTED.id AS Id,
                INSERTED.name AS Name,
                INSERTED.description AS Description,
                INSERTED.created_at AS CreatedAt,
                INSERTED.updated_at AS UpdatedAt
            VALUES
                (@Id, @OrgId, @Name, @Description,
                 TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'),
                 TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'));";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QuerySingleAsync<TeamDto>(sql, new
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Name = dto.Name.Trim(),
            Description = dto.Description
        });
    }

    public async Task<TeamDto?> UpdateTeamAsync(Guid orgId, Guid teamId, UpdateTeamRequestDto dto)
    {
        const string sql = @"
            UPDATE teams
            SET
                name = @Name,
                description = @Description,
                updated_at = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            WHERE org_id = @OrgId AND id = @TeamId;

            SELECT
                t.id AS Id,
                t.name AS Name,
                t.description AS Description,
                t.created_at AS CreatedAt,
                t.updated_at AS UpdatedAt
            FROM teams t
            WHERE t.org_id = @OrgId AND t.id = @TeamId;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<TeamDto>(sql, new
        {
            OrgId = orgId,
            TeamId = teamId,
            Name = dto.Name.Trim(),
            Description = dto.Description
        });
    }

    public async Task<bool> DeleteTeamAsync(Guid orgId, Guid teamId)
    {
        const string sql = @"DELETE FROM teams WHERE org_id = @orgId AND id = @teamId;";
        using var connection = _connectionFactory.CreateConnection();
        var affected = await connection.ExecuteAsync(sql, new { orgId, teamId });
        return affected > 0;
    }

    public async Task<bool> NhUserExistsInOrgAsync(Guid orgId, Guid nhUserId)
    {
        const string sql = @"
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM nh_users u WHERE u.org_id = @orgId AND u.id = @nhUserId
            ) THEN 1 ELSE 0 END;";

        using var connection = _connectionFactory.CreateConnection();
        var v = await connection.ExecuteScalarAsync<int>(sql, new { orgId, nhUserId });
        return v == 1;
    }

    public async Task<IReadOnlyList<TeamMemberDto>> ListMembersAsync(Guid orgId, Guid teamId)
    {
        const string sql = @"
            SELECT
                u.id AS UserId,
                u.first_name AS FirstName,
                u.last_name AS LastName,
                u.email AS Email,
                tm.is_team_lead AS IsTeamLead,
                tm.created_at AS JoinedAt
            FROM team_members tm
            INNER JOIN nh_users u ON u.id = tm.user_id AND u.org_id = @orgId
            WHERE tm.org_id = @orgId AND tm.team_id = @teamId
            ORDER BY u.last_name ASC, u.first_name ASC;";

        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<TeamMemberDto>(sql, new { orgId, teamId });
        return rows.ToList();
    }

    public async Task<bool> TeamMembershipExistsAsync(Guid orgId, Guid teamId, Guid userId)
    {
        const string sql = @"
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM team_members tm
                WHERE tm.org_id = @orgId AND tm.team_id = @teamId AND tm.user_id = @userId
            ) THEN 1 ELSE 0 END;";

        using var connection = _connectionFactory.CreateConnection();
        var v = await connection.ExecuteScalarAsync<int>(sql, new { orgId, teamId, userId });
        return v == 1;
    }

    public async Task<TeamMemberDto?> InsertMemberAsync(Guid orgId, Guid teamId, Guid userId, bool isTeamLead)
    {
        const string sql = @"
            INSERT INTO team_members (id, org_id, team_id, user_id, is_team_lead, created_at)
            VALUES (@id, @orgId, @teamId, @userId, @isTeamLead, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'));

            SELECT
                u.id AS UserId,
                u.first_name AS FirstName,
                u.last_name AS LastName,
                u.email AS Email,
                tm.is_team_lead AS IsTeamLead,
                tm.created_at AS JoinedAt
            FROM team_members tm
            INNER JOIN nh_users u ON u.id = tm.user_id AND u.org_id = @orgId
            WHERE tm.org_id = @orgId AND tm.team_id = @teamId AND tm.user_id = @userId;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<TeamMemberDto>(sql, new
        {
            id = Guid.NewGuid(),
            orgId,
            teamId,
            userId,
            isTeamLead
        });
    }

    public async Task<bool> DeleteMemberAsync(Guid orgId, Guid teamId, Guid userId)
    {
        const string sql = @"
            DELETE FROM team_members
            WHERE org_id = @orgId AND team_id = @teamId AND user_id = @userId;";

        using var connection = _connectionFactory.CreateConnection();
        var affected = await connection.ExecuteAsync(sql, new { orgId, teamId, userId });
        return affected > 0;
    }
}
