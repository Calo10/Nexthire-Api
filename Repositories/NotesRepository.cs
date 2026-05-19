using System.Data;
using System.Data.SqlClient;
using Dapper;
using nexthire_api.Data;
using nexthire_api.Models.Notes;

namespace nexthire_api.Repositories;

public class NotesRepository : INotesRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<NotesRepository> _logger;

    public NotesRepository(IDbConnectionFactory connectionFactory, ILogger<NotesRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<NoteDto>> GetByApplicationIdAsync(Guid orgId, Guid applicationId, int limit)
    {
        using var connection = _connectionFactory.CreateConnection();
        var schema = await GetNotesSchemaAsync(connection);

        var bodyExpr = GetBodySelectExpression(schema);
        var createdByCol = GetCreatedByColumn(schema);
        var createdAtExpr = schema.HasCreatedAt ? "n.created_at" : "TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')";
        var updatedAtExpr = schema.HasUpdatedAt ? "n.updated_at" : "NULL";

        var orderBy = schema.HasCreatedAt ? "n.created_at DESC, n.id DESC" : "n.id DESC";

        var sql = $@"
            SELECT TOP (@limit)
                n.id AS Id,
                n.application_id AS ApplicationId,
                {bodyExpr} AS Body,
                {(createdByCol is null ? "CAST(NULL AS uniqueidentifier)" : $"n.{createdByCol}")} AS CreatedByUserId,
                {(createdByCol is null ? "CAST(NULL AS nvarchar(400))" : "COALESCE(u.display_name, u.email)")} AS CreatedByName,
                {(createdByCol is null ? "CAST(NULL AS nvarchar(640))" : "u.email")} AS CreatedByEmail,
                {createdAtExpr} AS CreatedAt,
                {updatedAtExpr} AS UpdatedAt
            FROM notes n
            INNER JOIN applications a ON a.id = n.application_id AND a.org_id = @orgId
            {(createdByCol is null ? "" : $"LEFT JOIN nh_users u ON u.id = n.{createdByCol} AND u.org_id = @orgId")}
            WHERE n.application_id = @applicationId
            ORDER BY {orderBy};";

        var rows = await connection.QueryAsync<NoteDto>(sql, new { orgId, applicationId, limit });
        return rows.ToList();
    }

    public async Task<NoteDto?> GetByIdAsync(Guid orgId, Guid id)
    {
        using var connection = _connectionFactory.CreateConnection();
        var schema = await GetNotesSchemaAsync(connection);

        var bodyExpr = GetBodySelectExpression(schema);
        var createdByCol = GetCreatedByColumn(schema);
        var createdAtExpr = schema.HasCreatedAt ? "n.created_at" : "TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')";
        var updatedAtExpr = schema.HasUpdatedAt ? "n.updated_at" : "NULL";

        var sql = $@"
            SELECT TOP 1
                n.id AS Id,
                n.application_id AS ApplicationId,
                {bodyExpr} AS Body,
                {(createdByCol is null ? "CAST(NULL AS uniqueidentifier)" : $"n.{createdByCol}")} AS CreatedByUserId,
                {(createdByCol is null ? "CAST(NULL AS nvarchar(400))" : "COALESCE(u.display_name, u.email)")} AS CreatedByName,
                {(createdByCol is null ? "CAST(NULL AS nvarchar(640))" : "u.email")} AS CreatedByEmail,
                {createdAtExpr} AS CreatedAt,
                {updatedAtExpr} AS UpdatedAt
            FROM notes n
            INNER JOIN applications a ON a.id = n.application_id AND a.org_id = @orgId
            {(createdByCol is null ? "" : $"LEFT JOIN nh_users u ON u.id = n.{createdByCol} AND u.org_id = @orgId")}
            WHERE n.id = @id;";

        return await connection.QueryFirstOrDefaultAsync<NoteDto>(sql, new { orgId, id });
    }

    public async Task<NoteDto> CreateAsync(Guid orgId, Guid id, Guid applicationId, string body, Guid? createdByUserId)
    {
        using var connection = _connectionFactory.CreateConnection();
        var schema = await GetNotesSchemaAsync(connection);

        if (!schema.HasApplicationId)
            throw new InvalidOperationException("dbo.notes table is missing required column 'application_id'.");

        var bodyCol = GetBodyWriteColumn(schema);
        if (bodyCol is null)
            throw new InvalidOperationException("dbo.notes table is missing a supported body column (body/content/text/note).");

        var createdByCol = GetCreatedByColumn(schema);

        var insertColumns = new List<string> { "id", "application_id", bodyCol };
        var insertValues = new List<string> { "@id", "@applicationId", "@body" };
        var p = new DynamicParameters();
        p.Add("id", id);
        p.Add("applicationId", applicationId);
        p.Add("body", body);

        if (schema.HasOrgId)
        {
            insertColumns.Add("org_id");
            insertValues.Add("@orgId");
            p.Add("orgId", orgId);
        }

        if (createdByCol is not null)
        {
            insertColumns.Add(createdByCol);
            insertValues.Add("@createdByUserId");
            p.Add("createdByUserId", createdByUserId);
        }

        if (schema.HasCreatedAt)
        {
            insertColumns.Add("created_at");
            insertValues.Add("TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')");
        }

        if (schema.HasUpdatedAt)
        {
            insertColumns.Add("updated_at");
            insertValues.Add("TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')");
        }

        var insertSql = $@"
            INSERT INTO notes ({string.Join(", ", insertColumns)})
            VALUES ({string.Join(", ", insertValues)});";

        try
        {
            await connection.ExecuteAsync(insertSql, p);
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "Error inserting note. ApplicationId: {ApplicationId}", applicationId);
            throw;
        }

        var created = await GetByIdAsync(orgId, id);
        if (created is null)
            throw new InvalidOperationException("Note was created but could not be retrieved.");

        return created;
    }

    public async Task<bool> UpdateAsync(Guid orgId, Guid id, string body)
    {
        using var connection = _connectionFactory.CreateConnection();
        var schema = await GetNotesSchemaAsync(connection);

        var bodyCol = GetBodyWriteColumn(schema);
        if (bodyCol is null)
            throw new InvalidOperationException("dbo.notes table is missing a supported body column (body/content/text/note).");

        var setParts = new List<string> { $"n.{bodyCol} = @body" };
        if (schema.HasUpdatedAt)
            setParts.Add("n.updated_at = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')");

        var sql = $@"
            UPDATE n
            SET {string.Join(", ", setParts)}
            FROM notes n
            INNER JOIN applications a ON a.id = n.application_id AND a.org_id = @orgId
            WHERE n.id = @id;";

        var affected = await connection.ExecuteAsync(sql, new { orgId, id, body });
        return affected > 0;
    }

    public async Task<bool> DeleteAsync(Guid orgId, Guid id)
    {
        using var connection = _connectionFactory.CreateConnection();

        const string sql = @"
            DELETE n
            FROM notes n
            INNER JOIN applications a ON a.id = n.application_id AND a.org_id = @orgId
            WHERE n.id = @id;";

        var affected = await connection.ExecuteAsync(sql, new { orgId, id });
        return affected > 0;
    }

    private sealed record NotesSchema(
        bool HasOrgId,
        bool HasApplicationId,
        bool HasCreatedAt,
        bool HasUpdatedAt,
        bool HasBody,
        bool HasContent,
        bool HasText,
        bool HasNote,
        bool HasAuthorUserId,
        bool HasCreatedByUserId,
        bool HasCreatedByNhUserId,
        bool HasUserId);

    private async Task<NotesSchema> GetNotesSchemaAsync(IDbConnection connection)
    {
        const string sql = @"
            SELECT LOWER(COLUMN_NAME) AS ColumnName
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'notes';";

        var cols = (await connection.QueryAsync<string>(sql)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new NotesSchema(
            HasOrgId: cols.Contains("org_id"),
            HasApplicationId: cols.Contains("application_id"),
            HasCreatedAt: cols.Contains("created_at"),
            HasUpdatedAt: cols.Contains("updated_at"),
            HasBody: cols.Contains("body"),
            HasContent: cols.Contains("content"),
            HasText: cols.Contains("text"),
            HasNote: cols.Contains("note"),
            HasAuthorUserId: cols.Contains("author_user_id"),
            HasCreatedByUserId: cols.Contains("created_by_user_id"),
            HasCreatedByNhUserId: cols.Contains("created_by_nh_user_id"),
            HasUserId: cols.Contains("user_id"));
    }

    private static string GetBodySelectExpression(NotesSchema schema)
    {
        // If multiple exist, prefer body -> content -> text -> note
        if (schema.HasBody) return "n.body";
        if (schema.HasContent) return "n.content";
        if (schema.HasText) return "n.text";
        if (schema.HasNote) return "n.note";

        // Return empty string to avoid SQL errors; create/update will still throw.
        return "CAST('' AS nvarchar(max))";
    }

    private static string? GetBodyWriteColumn(NotesSchema schema)
    {
        if (schema.HasBody) return "body";
        if (schema.HasContent) return "content";
        if (schema.HasText) return "text";
        if (schema.HasNote) return "note";
        return null;
    }

    private static string? GetCreatedByColumn(NotesSchema schema)
    {
        if (schema.HasAuthorUserId) return "author_user_id";
        if (schema.HasCreatedByUserId) return "created_by_user_id";
        if (schema.HasCreatedByNhUserId) return "created_by_nh_user_id";
        if (schema.HasUserId) return "user_id";
        return null;
    }
}

