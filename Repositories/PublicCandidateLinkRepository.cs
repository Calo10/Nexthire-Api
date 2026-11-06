using System.Text.Json;
using Dapper;
using nexthire_api.Data;
using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public class PublicCandidateLinkRepository : IPublicCandidateLinkRepository
{
    private static int _schemaReady;
    private readonly IDbConnectionFactory _connectionFactory;

    public PublicCandidateLinkRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<Guid> InsertAsync(
        Guid orgId,
        Guid? createdBy,
        string name,
        string? search,
        string? source,
        DateTimeOffset? from,
        DateTimeOffset? to,
        IReadOnlyCollection<Guid> tagIds)
    {
        await EnsureSchemaAsync();
        var id = Guid.NewGuid();
        const string sql = @"
            INSERT INTO dbo.public_candidate_links
                (id, org_id, name, search, source, from_at, to_at, tag_ids_json, created_by, created_at)
            VALUES
                (@id, @orgId, @name, @search, @source, @from, @to, @tagIdsJson, @createdBy, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'));";

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(sql, new
        {
            id,
            orgId,
            name,
            search,
            source,
            from,
            to,
            tagIdsJson = JsonSerializer.Serialize(tagIds),
            createdBy
        });
        return id;
    }

    public async Task<PublicCandidateLinkRecord?> GetByIdAsync(Guid id)
    {
        await EnsureSchemaAsync();
        const string sql = @"
            SELECT
                id AS Id,
                org_id AS OrgId,
                name AS Name,
                search AS Search,
                source AS Source,
                from_at AS FromAt,
                to_at AS ToAt,
                tag_ids_json AS TagIdsJson
            FROM dbo.public_candidate_links
            WHERE id = @id;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<PublicCandidateLinkRecord>(sql, new { id });
    }

    public async Task<IReadOnlyList<PublicCandidateLinkAdminDto>> ListByOrgAsync(Guid orgId)
    {
        await EnsureSchemaAsync();
        const string sql = @"
            SELECT
                id AS Id,
                name AS Name,
                created_at AS CreatedAt
            FROM dbo.public_candidate_links
            WHERE org_id = @orgId
            ORDER BY created_at DESC, id DESC;";

        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<PublicCandidateLinkAdminDto>(sql, new { orgId });
        return rows.ToList();
    }

    public async Task<bool> DeleteAsync(Guid orgId, Guid id)
    {
        await EnsureSchemaAsync();
        const string sql = @"
            DELETE FROM dbo.public_candidate_links
            WHERE org_id = @orgId AND id = @id;";

        using var connection = _connectionFactory.CreateConnection();
        var affected = await connection.ExecuteAsync(sql, new { orgId, id });
        return affected > 0;
    }

    private async Task EnsureSchemaAsync()
    {
        if (Volatile.Read(ref _schemaReady) == 1)
            return;

        const string sql = @"
IF OBJECT_ID(N'dbo.public_candidate_links', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.public_candidate_links
    (
        id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_public_candidate_links PRIMARY KEY,
        org_id UNIQUEIDENTIFIER NOT NULL,
        name NVARCHAR(160) NOT NULL,
        search NVARCHAR(400) NULL,
        source NVARCHAR(160) NULL,
        from_at DATETIMEOFFSET NULL,
        to_at DATETIMEOFFSET NULL,
        tag_ids_json NVARCHAR(MAX) NOT NULL,
        created_by UNIQUEIDENTIFIER NULL,
        created_at DATETIMEOFFSET NOT NULL CONSTRAINT DF_public_candidate_links_created DEFAULT (TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'))
    );
    CREATE INDEX IX_public_candidate_links_org ON dbo.public_candidate_links (org_id);
END

IF COL_LENGTH(N'dbo.public_candidate_links', N'name') IS NULL
BEGIN
    ALTER TABLE dbo.public_candidate_links
        ADD name NVARCHAR(160) NOT NULL CONSTRAINT DF_public_candidate_links_name DEFAULT ('');
END";

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(sql);
        Volatile.Write(ref _schemaReady, 1);
    }
}
