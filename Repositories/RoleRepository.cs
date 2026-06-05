using System.Collections.Concurrent;
using System.Data;
using Dapper;
using nexthire_api.Data;
using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public class RoleRepository : IRoleRepository
{
    private static readonly ConcurrentDictionary<string, RolesSchema> RolesSchemaCache = new();
    private static readonly ConcurrentDictionary<string, UserRolesSchema> UserRolesSchemaCache = new();

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<RoleRepository> _logger;

    public RoleRepository(IDbConnectionFactory connectionFactory, ILogger<RoleRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task EnsureDefaultRolesAsync(Guid orgId)
    {
        using var connection = _connectionFactory.CreateConnection();
        var schema = await GetRolesSchemaAsync(connection);

        foreach (var (code, name) in DefaultRoles)
        {
            await EnsureRoleAsync(connection, schema, orgId, code, name);
        }
    }

    public async Task<IReadOnlyList<RoleDto>> ListRolesAsync(Guid orgId)
    {
        using var connection = _connectionFactory.CreateConnection();
        var schema = await GetRolesSchemaAsync(connection);
        var sql = BuildRoleSelectSql(schema, "WHERE r.org_id = @orgId ORDER BY r.code ASC");
        var rows = await connection.QueryAsync<RoleDto>(sql, new { orgId });
        return rows.ToList();
    }

    public async Task<RoleDto?> GetRoleByIdAsync(Guid orgId, Guid roleId)
    {
        using var connection = _connectionFactory.CreateConnection();
        var schema = await GetRolesSchemaAsync(connection);
        var sql = BuildRoleSelectSql(schema, "WHERE r.org_id = @orgId AND r.id = @roleId");
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
        using var connection = _connectionFactory.CreateConnection();
        var urSchema = await GetUserRolesSchemaAsync(connection);
        var assignedAt = urSchema.HasCreatedAt
            ? "ur.created_at AS AssignedAt"
            : "TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS AssignedAt";

        var sql = $@"
            SELECT
                ur.user_id AS UserId,
                ur.role_id AS RoleId,
                r.code AS RoleCode,
                r.name AS RoleName,
                {assignedAt}
            FROM user_roles ur
            INNER JOIN roles r ON r.id = ur.role_id AND r.org_id = @orgId
            WHERE ur.org_id = @orgId AND ur.user_id = @userId
            ORDER BY r.code ASC;";

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
        using var connection = _connectionFactory.CreateConnection();
        var urSchema = await GetUserRolesSchemaAsync(connection);

        var insertCols = new List<string>();
        var insertVals = new List<string>();
        var parameters = new DynamicParameters();
        parameters.Add("orgId", orgId);
        parameters.Add("userId", userId);
        parameters.Add("roleId", roleId);

        if (urSchema.HasId)
        {
            insertCols.Add("id");
            insertVals.Add("@id");
            parameters.Add("id", Guid.NewGuid());
        }

        if (urSchema.HasOrgId)
        {
            insertCols.Add("org_id");
            insertVals.Add("@orgId");
        }

        insertCols.Add("user_id");
        insertVals.Add("@userId");
        insertCols.Add("role_id");
        insertVals.Add("@roleId");

        if (urSchema.HasCreatedAt)
        {
            insertCols.Add("created_at");
            insertVals.Add("TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')");
        }

        var insertSql = $"INSERT INTO user_roles ({string.Join(", ", insertCols)}) VALUES ({string.Join(", ", insertVals)});";
        await connection.ExecuteAsync(insertSql, parameters);

        var roles = await ListUserRolesAsync(orgId, userId);
        return roles.FirstOrDefault(r => r.RoleId == roleId);
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

    private static readonly (string Code, string Name)[] DefaultRoles =
    [
        ("account_admin", "Account Admin"),
        ("supervisor", "Supervisor"),
        ("recruiter", "Recruiter")
    ];

    private static async Task EnsureRoleAsync(
        IDbConnection connection,
        RolesSchema schema,
        Guid orgId,
        string code,
        string name)
    {
        var exists = await connection.ExecuteScalarAsync<int>(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM roles r WHERE r.org_id = @orgId AND r.code = @code) THEN 1 ELSE 0 END;",
            new { orgId, code });
        if (exists == 1)
            return;

        var id = Guid.NewGuid();
        var cols = new List<string> { "id", "org_id", "code", "name" };
        var vals = new List<string> { "@id", "@orgId", "@code", "@name" };
        var parameters = new DynamicParameters();
        parameters.Add("id", id);
        parameters.Add("orgId", orgId);
        parameters.Add("code", code);
        parameters.Add("name", name);

        if (schema.HasDescription)
        {
            cols.Add("description");
            vals.Add("NULL");
        }

        if (schema.HasCreatedAt)
        {
            cols.Add("created_at");
            vals.Add("TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')");
        }

        if (schema.HasUpdatedAt)
        {
            cols.Add("updated_at");
            vals.Add("TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')");
        }

        var sql = $"INSERT INTO roles ({string.Join(", ", cols)}) VALUES ({string.Join(", ", vals)});";
        await connection.ExecuteAsync(sql, parameters);
    }

    private static string BuildRoleSelectSql(RolesSchema schema, string whereClause)
    {
        var desc = schema.HasDescription ? "r.description AS Description" : "CAST(NULL AS nvarchar(max)) AS Description";
        var created = schema.HasCreatedAt
            ? "r.created_at AS CreatedAt"
            : "TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS CreatedAt";
        var updated = schema.HasUpdatedAt
            ? "r.updated_at AS UpdatedAt"
            : "TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS UpdatedAt";

        return $@"
            SELECT
                r.id AS Id,
                r.code AS Code,
                r.name AS Name,
                {desc},
                {created},
                {updated}
            FROM roles r
            {whereClause};";
    }

    private async Task<RolesSchema> GetRolesSchemaAsync(IDbConnection connection)
    {
        var key = (connection.ConnectionString ?? "default") + ":roles";
        if (RolesSchemaCache.TryGetValue(key, out var cached))
            return cached;

        var cols = await GetColumnSetAsync(connection, "roles");
        var schema = new RolesSchema(
            HasDescription: cols.Contains("description"),
            HasCreatedAt: cols.Contains("created_at"),
            HasUpdatedAt: cols.Contains("updated_at"));

        RolesSchemaCache[key] = schema;
        return schema;
    }

    private async Task<UserRolesSchema> GetUserRolesSchemaAsync(IDbConnection connection)
    {
        var key = (connection.ConnectionString ?? "default") + ":user_roles";
        if (UserRolesSchemaCache.TryGetValue(key, out var cached))
            return cached;

        var cols = await GetColumnSetAsync(connection, "user_roles");
        var schema = new UserRolesSchema(
            HasId: cols.Contains("id"),
            HasOrgId: cols.Contains("org_id"),
            HasCreatedAt: cols.Contains("created_at"));

        UserRolesSchemaCache[key] = schema;
        return schema;
    }

    private static async Task<HashSet<string>> GetColumnSetAsync(IDbConnection connection, string tableName)
    {
        const string sql = @"
            SELECT LOWER(COLUMN_NAME) AS ColumnName
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = @tableName;";

        var cols = await connection.QueryAsync<string>(sql, new { tableName });
        return cols.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record RolesSchema(bool HasDescription, bool HasCreatedAt, bool HasUpdatedAt);

    private sealed record UserRolesSchema(bool HasId, bool HasOrgId, bool HasCreatedAt);
}
