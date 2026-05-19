using Dapper;
using nexthire_api.Data;
using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public class RoleRepository : IRoleRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<RoleRepository> _logger;

    public RoleRepository(IDbConnectionFactory connectionFactory, ILogger<RoleRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task EnsureDefaultRolesAsync(Guid orgId)
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();
        const string sql = @"
            INSERT INTO roles (id, org_id, code, name, description, created_at, updated_at)
            SELECT @id1, @orgId, N'account_admin', N'Account Admin', NULL,
                TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'), TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            WHERE NOT EXISTS (SELECT 1 FROM roles r WHERE r.org_id = @orgId AND r.code = N'account_admin');

            INSERT INTO roles (id, org_id, code, name, description, created_at, updated_at)
            SELECT @id2, @orgId, N'supervisor', N'Supervisor', NULL,
                TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'), TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            WHERE NOT EXISTS (SELECT 1 FROM roles r WHERE r.org_id = @orgId AND r.code = N'supervisor');

            INSERT INTO roles (id, org_id, code, name, description, created_at, updated_at)
            SELECT @id3, @orgId, N'recruiter', N'Recruiter', NULL,
                TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'), TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            WHERE NOT EXISTS (SELECT 1 FROM roles r WHERE r.org_id = @orgId AND r.code = N'recruiter');";

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(sql, new { orgId, id1, id2, id3 });
    }

    public async Task<IReadOnlyList<RoleDto>> ListRolesAsync(Guid orgId)
    {
        const string sql = @"
            SELECT
                r.id AS Id,
                r.code AS Code,
                r.name AS Name,
                r.description AS Description,
                r.created_at AS CreatedAt,
                r.updated_at AS UpdatedAt
            FROM roles r
            WHERE r.org_id = @orgId
            ORDER BY r.code ASC;";

        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<RoleDto>(sql, new { orgId });
        return rows.ToList();
    }

    public async Task<RoleDto?> GetRoleByIdAsync(Guid orgId, Guid roleId)
    {
        const string sql = @"
            SELECT
                r.id AS Id,
                r.code AS Code,
                r.name AS Name,
                r.description AS Description,
                r.created_at AS CreatedAt,
                r.updated_at AS UpdatedAt
            FROM roles r
            WHERE r.org_id = @orgId AND r.id = @roleId;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<RoleDto>(sql, new { orgId, roleId });
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

    public async Task<IReadOnlyList<UserRoleDto>> ListUserRolesAsync(Guid orgId, Guid userId)
    {
        const string sql = @"
            SELECT
                ur.user_id AS UserId,
                ur.role_id AS RoleId,
                r.code AS RoleCode,
                r.name AS RoleName,
                ur.created_at AS AssignedAt
            FROM user_roles ur
            INNER JOIN roles r ON r.id = ur.role_id AND r.org_id = @orgId
            WHERE ur.org_id = @orgId AND ur.user_id = @userId
            ORDER BY r.code ASC;";

        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<UserRoleDto>(sql, new { orgId, userId });
        return rows.ToList();
    }

    public async Task<bool> UserRoleExistsAsync(Guid orgId, Guid userId, Guid roleId)
    {
        const string sql = @"
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM user_roles ur
                WHERE ur.org_id = @orgId AND ur.user_id = @userId AND ur.role_id = @roleId
            ) THEN 1 ELSE 0 END;";

        using var connection = _connectionFactory.CreateConnection();
        var v = await connection.ExecuteScalarAsync<int>(sql, new { orgId, userId, roleId });
        return v == 1;
    }

    public async Task<UserRoleDto?> InsertUserRoleAsync(Guid orgId, Guid userId, Guid roleId)
    {
        const string sql = @"
            INSERT INTO user_roles (id, org_id, user_id, role_id, created_at)
            VALUES (@id, @orgId, @userId, @roleId, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'));

            SELECT
                ur.user_id AS UserId,
                ur.role_id AS RoleId,
                r.code AS RoleCode,
                r.name AS RoleName,
                ur.created_at AS AssignedAt
            FROM user_roles ur
            INNER JOIN roles r ON r.id = ur.role_id AND r.org_id = @orgId
            WHERE ur.org_id = @orgId AND ur.user_id = @userId AND ur.role_id = @roleId;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<UserRoleDto>(sql, new
        {
            id = Guid.NewGuid(),
            orgId,
            userId,
            roleId
        });
    }

    public async Task<bool> DeleteUserRoleAsync(Guid orgId, Guid userId, Guid roleId)
    {
        const string sql = @"
            DELETE FROM user_roles
            WHERE org_id = @orgId AND user_id = @userId AND role_id = @roleId;";

        using var connection = _connectionFactory.CreateConnection();
        var affected = await connection.ExecuteAsync(sql, new { orgId, userId, roleId });
        return affected > 0;
    }
}
