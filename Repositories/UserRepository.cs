using System.Collections.Concurrent;
using System.Data;
using Dapper;
using nexthire_api.Data;
using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public class UserRepository : IUserRepository
{
    private static readonly ConcurrentDictionary<string, NhUsersSchema> SchemaCache = new();

    private readonly IDbConnectionFactory _connectionFactory;

    public UserRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task EnsureSchemaAsync()
    {
        const string sql = @"
            IF EXISTS (
                SELECT 1
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = 'dbo'
                  AND TABLE_NAME = 'nh_users'
                  AND COLUMN_NAME = 'nexa_user_id'
                  AND IS_NULLABLE = 'NO'
            )
            BEGIN
                ALTER TABLE dbo.nh_users ALTER COLUMN nexa_user_id uniqueidentifier NULL;
            END;

            -- Allow multiple pending invites (nexa_user_id NULL). SQL Server unique indexes permit only one NULL.
            IF EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE name = 'UX_nh_users_nexa_user_id'
                  AND object_id = OBJECT_ID('dbo.nh_users')
                  AND has_filter = 0
            )
            BEGIN
                DROP INDEX UX_nh_users_nexa_user_id ON dbo.nh_users;
            END;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE name = 'UX_nh_users_nexa_user_id'
                  AND object_id = OBJECT_ID('dbo.nh_users')
            )
            BEGIN
                CREATE UNIQUE NONCLUSTERED INDEX UX_nh_users_nexa_user_id
                    ON dbo.nh_users (nexa_user_id)
                    WHERE nexa_user_id IS NOT NULL;
            END";

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(sql);
        SchemaCache.Clear();
    }

    public async Task<IReadOnlyList<OrgUserDto>> ListByOrgAsync(Guid orgId)
    {
        using var connection = _connectionFactory.CreateConnection();
        var schema = await GetSchemaAsync(connection);
        var sql = BuildSelectSql(schema, "WHERE u.org_id = @orgId ORDER BY u.created_at DESC");
        var users = (await connection.QueryAsync<OrgUserRow>(sql, new { orgId })).ToList();
        return await AttachRolesAsync(connection, orgId, users);
    }

    public async Task<OrgUserDto?> GetByIdAsync(Guid orgId, Guid userId)
    {
        using var connection = _connectionFactory.CreateConnection();
        var schema = await GetSchemaAsync(connection);
        var sql = BuildSelectSql(schema, "WHERE u.org_id = @orgId AND u.id = @userId");
        var row = await connection.QueryFirstOrDefaultAsync<OrgUserRow>(sql, new { orgId, userId });
        if (row is null)
            return null;

        var list = await AttachRolesAsync(connection, orgId, [row]);
        return list.FirstOrDefault();
    }

    public async Task<OrgUserDto?> GetByEmailAsync(Guid orgId, string email)
    {
        using var connection = _connectionFactory.CreateConnection();
        var schema = await GetSchemaAsync(connection);
        var sql = BuildSelectSql(schema, "WHERE u.org_id = @orgId AND LOWER(u.email) = LOWER(@email)");
        var row = await connection.QueryFirstOrDefaultAsync<OrgUserRow>(sql, new { orgId, email });
        if (row is null)
            return null;

        var list = await AttachRolesAsync(connection, orgId, [row]);
        return list.FirstOrDefault();
    }

    public async Task<Guid> UpsertPendingByEmailAsync(Guid orgId, string email, string? firstName, string? lastName, string? phone)
    {
        var existing = await GetByEmailAsync(orgId, email);
        if (existing is not null)
        {
            if (!string.IsNullOrWhiteSpace(firstName) || !string.IsNullOrWhiteSpace(lastName) || !string.IsNullOrWhiteSpace(phone))
            {
                await UpdateProfileAsync(orgId, existing.Id, new UpdateOrgUserRequestDto
                {
                    FirstName = firstName,
                    LastName = lastName,
                    Phone = phone
                });
            }

            return existing.Id;
        }

        var (fn, ln) = (firstName, lastName);
        var displayName = BuildDisplayName(fn, ln, email);
        return await InsertUserAsync(orgId, null, email, fn, ln, displayName, phone);
    }

    public async Task<Guid> UpsertFromNexaMemberAsync(Guid orgId, Guid nexaUserId, string email, string? fullName)
    {
        var (firstName, lastName) = SplitName(fullName, email);
        var displayName = BuildDisplayName(firstName, lastName, email, fullName);
        using var connection = _connectionFactory.CreateConnection();
        var schema = await GetSchemaAsync(connection);

        var findClauses = new List<string>();
        var findParams = new DynamicParameters();
        findParams.Add("orgId", orgId);
        if (schema.HasOrgId)
        {
            findClauses.Add("org_id = @orgId");
        }

        if (schema.HasNexaUserId)
        {
            findClauses.Add("(nexa_user_id = @nexaUserId OR LOWER(email) = LOWER(@email))");
            findParams.Add("nexaUserId", nexaUserId);
            findParams.Add("email", email);
        }
        else if (schema.HasEmail)
        {
            findClauses.Add("LOWER(email) = LOWER(@email)");
            findParams.Add("email", email);
        }

        if (findClauses.Count > 0)
        {
            var findSql = $"SELECT TOP 1 id FROM nh_users WHERE {string.Join(" AND ", findClauses)};";
            var existingId = await connection.ExecuteScalarAsync<Guid?>(findSql, findParams);
            if (existingId.HasValue)
            {
                await ExecuteUpdateAsync(connection, schema, existingId.Value, orgId, nexaUserId, email, firstName, lastName, displayName, null);
                return existingId.Value;
            }
        }

        return await InsertUserAsync(orgId, nexaUserId, email, firstName, lastName, displayName, null);
    }

    public async Task<OrgUserDto?> UpdateProfileAsync(Guid orgId, Guid userId, UpdateOrgUserRequestDto dto)
    {
        using var connection = _connectionFactory.CreateConnection();
        var schema = await GetSchemaAsync(connection);
        var row = await connection.QueryFirstOrDefaultAsync<OrgUserRow>(
            BuildSelectSql(schema, "WHERE u.org_id = @orgId AND u.id = @userId"),
            new { orgId, userId });
        if (row is null)
            return null;

        var firstName = string.IsNullOrWhiteSpace(dto.FirstName) ? row.FirstName : dto.FirstName.Trim();
        var lastName = string.IsNullOrWhiteSpace(dto.LastName) ? row.LastName : dto.LastName.Trim();
        var displayName = BuildDisplayName(firstName, lastName, row.Email);
        await ExecuteUpdateAsync(connection, schema, userId, orgId, row.NexaUserId, row.Email, firstName, lastName, displayName, dto.Phone);

        var updated = await connection.QueryFirstOrDefaultAsync<OrgUserRow>(
            BuildSelectSql(schema, "WHERE u.org_id = @orgId AND u.id = @userId"),
            new { orgId, userId });
        if (updated is null)
            return null;

        var list = await AttachRolesAsync(connection, orgId, [updated]);
        return list.FirstOrDefault();
    }

    public async Task<bool> DeleteFromOrgAsync(Guid orgId, Guid userId)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var tx = connection.BeginTransaction();

        await connection.ExecuteAsync(
            "DELETE FROM team_members WHERE org_id = @orgId AND user_id = @userId;",
            new { orgId, userId }, tx);
        await connection.ExecuteAsync(
            "DELETE FROM user_roles WHERE org_id = @orgId AND user_id = @userId;",
            new { orgId, userId }, tx);
        var affected = await connection.ExecuteAsync(
            "DELETE FROM nh_users WHERE org_id = @orgId AND id = @userId;",
            new { orgId, userId }, tx);

        tx.Commit();
        return affected > 0;
    }

    private async Task<Guid> InsertUserAsync(
        Guid orgId,
        Guid? nexaUserId,
        string email,
        string? firstName,
        string? lastName,
        string displayName,
        string? phone)
    {
        using var connection = _connectionFactory.CreateConnection();
        var schema = await GetSchemaAsync(connection);
        var id = Guid.NewGuid();
        var safeEmail = email.Trim().ToLowerInvariant();
        var fn = string.IsNullOrWhiteSpace(firstName) ? "User" : firstName.Trim();
        var ln = string.IsNullOrWhiteSpace(lastName) ? string.Empty : lastName.Trim();

        var insertCols = new List<string> { "id" };
        var insertVals = new List<string> { "@id" };
        var parameters = new DynamicParameters();
        parameters.Add("id", id);

        if (schema.HasOrgId)
        {
            insertCols.Add("org_id");
            insertVals.Add("@orgId");
            parameters.Add("orgId", orgId);
        }

        if (schema.HasNexaUserId)
        {
            insertCols.Add("nexa_user_id");
            insertVals.Add("@nexaUserId");
            parameters.Add("nexaUserId", nexaUserId);
        }

        if (schema.HasEmail)
        {
            insertCols.Add("email");
            insertVals.Add("@email");
            parameters.Add("email", safeEmail);
        }

        if (schema.HasDisplayName)
        {
            insertCols.Add("display_name");
            insertVals.Add("@displayName");
            parameters.Add("displayName", displayName);
        }
        else
        {
            if (schema.HasFirstName)
            {
                insertCols.Add("first_name");
                insertVals.Add("@firstName");
                parameters.Add("firstName", fn);
            }

            if (schema.HasLastName)
            {
                insertCols.Add("last_name");
                insertVals.Add("@lastName");
                parameters.Add("lastName", ln);
            }
        }

        if (schema.HasPhone && !string.IsNullOrWhiteSpace(phone))
        {
            insertCols.Add("phone");
            insertVals.Add("@phone");
            parameters.Add("phone", phone.Trim());
        }

        if (schema.HasCreatedAt)
        {
            insertCols.Add("created_at");
            insertVals.Add("TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')");
        }

        if (schema.HasUpdatedAt)
        {
            insertCols.Add("updated_at");
            insertVals.Add("TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')");
        }

        var sql = $"INSERT INTO nh_users ({string.Join(", ", insertCols)}) VALUES ({string.Join(", ", insertVals)});";
        await connection.ExecuteAsync(sql, parameters);
        return id;
    }

    private static async Task ExecuteUpdateAsync(
        IDbConnection connection,
        NhUsersSchema schema,
        Guid id,
        Guid orgId,
        Guid? nexaUserId,
        string email,
        string firstName,
        string lastName,
        string displayName,
        string? phone)
    {
        var sets = new List<string>();
        var parameters = new DynamicParameters();
        parameters.Add("id", id);
        parameters.Add("orgId", orgId);

        if (schema.HasNexaUserId && nexaUserId.HasValue)
        {
            sets.Add("nexa_user_id = @nexaUserId");
            parameters.Add("nexaUserId", nexaUserId.Value);
        }

        if (schema.HasEmail)
        {
            sets.Add("email = @email");
            parameters.Add("email", email.Trim().ToLowerInvariant());
        }

        if (schema.HasDisplayName)
        {
            sets.Add("display_name = @displayName");
            parameters.Add("displayName", displayName);
        }
        else
        {
            if (schema.HasFirstName)
            {
                sets.Add("first_name = @firstName");
                parameters.Add("firstName", firstName);
            }

            if (schema.HasLastName)
            {
                sets.Add("last_name = @lastName");
                parameters.Add("lastName", lastName);
            }
        }

        if (schema.HasPhone && phone is not null)
        {
            sets.Add("phone = @phone");
            parameters.Add("phone", string.IsNullOrWhiteSpace(phone) ? null : phone.Trim());
        }

        if (schema.HasUpdatedAt)
        {
            sets.Add("updated_at = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')");
        }

        if (sets.Count == 0)
            return;

        var sql = $"UPDATE nh_users SET {string.Join(", ", sets)} WHERE id = @id AND org_id = @orgId;";
        await connection.ExecuteAsync(sql, parameters);
    }

    private static string BuildSelectSql(NhUsersSchema schema, string whereClause)
    {
        var nameCols = schema.HasFirstName && schema.HasLastName
            ? "u.first_name AS FirstName, u.last_name AS LastName"
            : schema.HasDisplayName
                ? "u.display_name AS RawDisplayName, CAST('' AS nvarchar(100)) AS FirstName, CAST('' AS nvarchar(100)) AS LastName"
                : "CAST('' AS nvarchar(100)) AS FirstName, CAST('' AS nvarchar(100)) AS LastName";

        var phoneCol = schema.HasPhone ? "u.phone AS Phone" : "CAST(NULL AS nvarchar(50)) AS Phone";
        var nexaCol = schema.HasNexaUserId ? "u.nexa_user_id AS NexaUserId" : "CAST(NULL AS uniqueidentifier) AS NexaUserId";
        var createdCol = schema.HasCreatedAt
            ? "u.created_at AS CreatedAt"
            : "TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS CreatedAt";

        return $@"
            SELECT
                u.id AS Id,
                {nexaCol},
                u.email AS Email,
                {nameCols},
                {phoneCol},
                {createdCol}
            FROM nh_users u
            {whereClause};";
    }

    private static (string FirstName, string LastName) SplitName(string? fullName, string email)
    {
        if (!string.IsNullOrWhiteSpace(fullName))
        {
            var parts = fullName.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            return (parts[0], parts.Length == 2 ? parts[1] : string.Empty);
        }

        var local = email.Split('@')[0];
        return (local, string.Empty);
    }

    private static string BuildDisplayName(string? firstName, string? lastName, string email, string? fullName = null)
    {
        if (!string.IsNullOrWhiteSpace(fullName))
            return fullName.Trim();

        var parts = new[] { firstName, lastName }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim());
        var joined = string.Join(' ', parts);
        return string.IsNullOrWhiteSpace(joined) ? email : joined;
    }

    private async Task<NhUsersSchema> GetSchemaAsync(IDbConnection connection)
    {
        var key = connection.ConnectionString ?? "default";
        if (SchemaCache.TryGetValue(key, out var cached))
            return cached;

        const string sql = @"
            SELECT LOWER(COLUMN_NAME) AS ColumnName
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'nh_users';";

        var cols = (await connection.QueryAsync<string>(sql)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var schema = new NhUsersSchema(
            HasOrgId: cols.Contains("org_id"),
            HasNexaUserId: cols.Contains("nexa_user_id"),
            HasEmail: cols.Contains("email"),
            HasDisplayName: cols.Contains("display_name"),
            HasFirstName: cols.Contains("first_name"),
            HasLastName: cols.Contains("last_name"),
            HasPhone: cols.Contains("phone"),
            HasCreatedAt: cols.Contains("created_at"),
            HasUpdatedAt: cols.Contains("updated_at"));

        SchemaCache[key] = schema;
        return schema;
    }

    private static async Task<IReadOnlyList<OrgUserDto>> AttachRolesAsync(
        IDbConnection connection,
        Guid orgId,
        IReadOnlyList<OrgUserRow> rows)
    {
        if (rows.Count == 0)
            return [];

        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.RawDisplayName)
                && string.IsNullOrWhiteSpace(row.FirstName)
                && string.IsNullOrWhiteSpace(row.LastName))
            {
                var (fn, ln) = SplitName(row.RawDisplayName, row.Email);
                row.FirstName = fn;
                row.LastName = ln;
            }
        }

        var userIds = rows.Select(r => r.Id).ToList();
        const string rolesSql = @"
            SELECT
                ur.user_id AS UserId,
                ur.role_id AS RoleId,
                r.code AS RoleCode,
                r.name AS RoleName,
                ur.created_at AS AssignedAt
            FROM user_roles ur
            INNER JOIN roles r ON r.id = ur.role_id AND r.org_id = @orgId
            WHERE ur.org_id = @orgId AND ur.user_id IN @userIds;";

        var roleRows = (await connection.QueryAsync<UserRoleDto>(rolesSql, new { orgId, userIds })).ToList();
        var rolesByUser = roleRows.GroupBy(r => r.UserId).ToDictionary(g => g.Key, g => g.ToList());

        return rows.Select(r => new OrgUserDto
        {
            Id = r.Id,
            NexaUserId = r.NexaUserId,
            Email = r.Email,
            FirstName = r.FirstName,
            LastName = r.LastName,
            Phone = r.Phone,
            Status = r.NexaUserId.HasValue ? "active" : "invited",
            Roles = rolesByUser.TryGetValue(r.Id, out var roles) ? roles : [],
            CreatedAt = r.CreatedAt
        }).ToList();
    }

    private sealed record NhUsersSchema(
        bool HasOrgId,
        bool HasNexaUserId,
        bool HasEmail,
        bool HasDisplayName,
        bool HasFirstName,
        bool HasLastName,
        bool HasPhone,
        bool HasCreatedAt,
        bool HasUpdatedAt);

    private sealed class OrgUserRow
    {
        public Guid Id { get; set; }
        public Guid? NexaUserId { get; set; }
        public string Email { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string? RawDisplayName { get; set; }
        public string? Phone { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }
}
