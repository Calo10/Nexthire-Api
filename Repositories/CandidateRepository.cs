using System.Data;
using System.Data.SqlClient;
using Dapper;
using nexthire_api.Data;
using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public class CandidateRepository : ICandidateRepository
{
    private const int MaxTagsPerCandidate = 20;
    private static int _tagsSchemaReady;
    private static int _notesSchemaReady;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<CandidateRepository> _logger;

    public CandidateRepository(IDbConnectionFactory connectionFactory, ILogger<CandidateRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<PagedResult<CandidateListItemDto>> GetPagedAsync(
        Guid orgId,
        string? search,
        string? source,
        DateTimeOffset? from,
        DateTimeOffset? to,
        IReadOnlyCollection<Guid>? tagIds,
        int page,
        int pageSize,
        string sort,
        string dir)
    {
        var trimmedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var phoneDigits = trimmedSearch == null
            ? string.Empty
            : new string(trimmedSearch.Where(char.IsDigit).ToArray());
        var trimmedSource = string.IsNullOrWhiteSpace(source) ? null : source.Trim();
        var tagIdList = (tagIds ?? Array.Empty<Guid>()).Where(id => id != Guid.Empty).Distinct().ToArray();
        var parameters = new DynamicParameters();
        parameters.Add("orgId", orgId);
        parameters.Add("search", trimmedSearch);
        parameters.Add("phoneDigits", phoneDigits);
        parameters.Add("source", trimmedSource);
        parameters.Add("from", from);
        parameters.Add("to", to);
        var tagIn = new List<string>(tagIdList.Length);
        for (var i = 0; i < tagIdList.Length; i++)
        {
            var name = $"tagId{i}";
            parameters.Add(name, tagIdList[i]);
            tagIn.Add("@" + name);
        }
        var tagFilter = tagIn.Count == 0
            ? string.Empty
            : $@"
              AND EXISTS (
                    SELECT 1
                    FROM candidate_tag_assignments cta
                    WHERE cta.candidate_id = c.id
                      AND cta.tag_id IN ({string.Join(", ", tagIn)})
                  )";

        var sortNormalized = string.IsNullOrWhiteSpace(sort) ? "created_at" : sort.Trim().ToLowerInvariant();
        if (sortNormalized != "created_at")
            throw new ArgumentException("Unsupported sort field. Allowed: created_at", nameof(sort));

        var dirNormalized = string.IsNullOrWhiteSpace(dir) ? "desc" : dir.Trim().ToLowerInvariant();
        var orderDir = dirNormalized switch
        {
            "asc" => "ASC",
            "desc" => "DESC",
            _ => throw new ArgumentException("Unsupported sort direction. Allowed: asc, desc", nameof(dir))
        };

        await EnsureTagsSchemaAsync();

        var offset = (page - 1) * pageSize;
        parameters.Add("offset", offset);
        parameters.Add("pageSize", pageSize);

        var sql = $@"
            SELECT COUNT(1)
            FROM candidates c
            WHERE c.org_id = @orgId
              AND (@source IS NULL OR c.source = @source)
              AND (@from IS NULL OR c.created_at >= @from)
              AND (@to IS NULL OR c.created_at <= @to)
              AND (
                    @search IS NULL
                    OR LOWER(c.first_name) LIKE '%' + LOWER(@search) + '%'
                    OR LOWER(c.last_name) LIKE '%' + LOWER(@search) + '%'
                    OR LOWER(CONCAT(c.first_name, ' ', c.last_name)) LIKE '%' + LOWER(@search) + '%'
                    OR LOWER(c.email) LIKE '%' + LOWER(@search) + '%'
                    OR LOWER(ISNULL(c.phone, '')) LIKE '%' + LOWER(@search) + '%'
                    OR (
                         @phoneDigits <> ''
                         AND REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(ISNULL(c.phone, ''), ' ', ''), '-', ''), '(', ''), ')', ''), '+', '') LIKE '%' + @phoneDigits + '%'
                       )
                  )
              {tagFilter};

            SELECT
                c.id AS Id,
                c.first_name AS FirstName,
                c.last_name AS LastName,
                c.email AS Email,
                c.phone AS Phone,
                c.source AS Source,
                c.resume_url AS ResumeUrl,
                c.created_at AS CreatedAt,
                c.updated_at AS UpdatedAt
            FROM candidates c
            WHERE c.org_id = @orgId
              AND (@source IS NULL OR c.source = @source)
              AND (@from IS NULL OR c.created_at >= @from)
              AND (@to IS NULL OR c.created_at <= @to)
              AND (
                    @search IS NULL
                    OR LOWER(c.first_name) LIKE '%' + LOWER(@search) + '%'
                    OR LOWER(c.last_name) LIKE '%' + LOWER(@search) + '%'
                    OR LOWER(CONCAT(c.first_name, ' ', c.last_name)) LIKE '%' + LOWER(@search) + '%'
                    OR LOWER(c.email) LIKE '%' + LOWER(@search) + '%'
                    OR LOWER(ISNULL(c.phone, '')) LIKE '%' + LOWER(@search) + '%'
                    OR (
                         @phoneDigits <> ''
                         AND REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(ISNULL(c.phone, ''), ' ', ''), '-', ''), '(', ''), ')', ''), '+', '') LIKE '%' + @phoneDigits + '%'
                       )
                  )
              {tagFilter}
            ORDER BY c.created_at {orderDir}, c.id {orderDir}
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;";

        using var connection = _connectionFactory.CreateConnection();
        int total;
        List<CandidateListItemDto> items;
        using (var multi = await connection.QueryMultipleAsync(sql, parameters))
        {
            total = await multi.ReadSingleAsync<int>();
            items = (await multi.ReadAsync<CandidateListItemDto>()).ToList();
        }

        await AttachTagsAsync(connection, orgId, items);

        return new PagedResult<CandidateListItemDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            Total = total
        };
    }

    public async Task<CandidateDto?> GetByIdAsync(Guid orgId, Guid id)
    {
        const string sql = @"
            SELECT
                c.id AS Id,
                c.first_name AS FirstName,
                c.last_name AS LastName,
                c.email AS Email,
                c.phone AS Phone,
                c.source AS Source,
                c.resume_url AS ResumeUrl,
                c.dynamic_answers_json AS DynamicAnswersJson,
                c.created_at AS CreatedAt,
                c.updated_at AS UpdatedAt
            FROM candidates c
            WHERE c.org_id = @orgId AND c.id = @id;";

        await EnsureTagsSchemaAsync();

        using var connection = _connectionFactory.CreateConnection();
        var candidate = await connection.QueryFirstOrDefaultAsync<CandidateDto>(sql, new { orgId, id });
        if (candidate == null)
            return null;

        await AttachTagsAsync(connection, orgId, new[] { candidate });
        return candidate;
    }

    public async Task<CandidateDto?> GetByEmailAsync(Guid orgId, string emailLower)
    {
        const string sql = @"
            SELECT TOP 1
                c.id AS Id,
                c.first_name AS FirstName,
                c.last_name AS LastName,
                c.email AS Email,
                c.phone AS Phone,
                c.source AS Source,
                c.resume_url AS ResumeUrl,
                c.created_at AS CreatedAt,
                c.updated_at AS UpdatedAt
            FROM candidates c
            WHERE c.org_id = @orgId AND LOWER(c.email) = @emailLower;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<CandidateDto>(sql, new { orgId, emailLower });
    }

    public async Task<CandidateDto?> GetByPhoneDigitsAsync(Guid orgId, string phoneDigits)
    {
        if (string.IsNullOrWhiteSpace(phoneDigits))
            return null;

        const string sql = @"
            SELECT TOP 1
                c.id AS Id,
                c.first_name AS FirstName,
                c.last_name AS LastName,
                c.email AS Email,
                c.phone AS Phone,
                c.source AS Source,
                c.resume_url AS ResumeUrl,
                c.created_at AS CreatedAt,
                c.updated_at AS UpdatedAt
            FROM candidates c
            WHERE c.org_id = @orgId
              AND c.phone IS NOT NULL
              AND LEN(@phoneDigits) > 0
              AND (
                    REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(ISNULL(c.phone, ''), ' ', ''), '-', ''), '(', ''), ')', ''), '+', '') = @phoneDigits
                  );";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<CandidateDto>(sql, new { orgId, phoneDigits });
    }

    public async Task<Guid?> GetOrgIdByCandidateIdAsync(Guid candidateId)
    {
        const string sql = @"SELECT org_id FROM candidates WHERE id = @candidateId;";
        using var connection = _connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<Guid?>(sql, new { candidateId });
    }

    public async Task<bool> ExistsByEmailAsync(Guid orgId, string emailLower, Guid? excludeId = null)
    {
        const string sql = @"
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM candidates c
                WHERE c.org_id = @orgId
                  AND LOWER(c.email) = @emailLower
                  AND (@excludeId IS NULL OR c.id <> @excludeId)
            ) THEN 1 ELSE 0 END;";

        using var connection = _connectionFactory.CreateConnection();
        var exists = await connection.ExecuteScalarAsync<int>(sql, new { orgId, emailLower, excludeId });
        return exists == 1;
    }

    public async Task<CandidateDto> InsertAsync(Guid orgId, CreateCandidateRequestDto dto, string emailLower, IDbTransaction? transaction = null)
    {
        const string sql = @"
            INSERT INTO candidates (id, org_id, first_name, last_name, email, phone, source, resume_url, created_at, updated_at)
            OUTPUT
                INSERTED.id AS Id,
                INSERTED.first_name AS FirstName,
                INSERTED.last_name AS LastName,
                INSERTED.email AS Email,
                INSERTED.phone AS Phone,
                INSERTED.source AS Source,
                INSERTED.resume_url AS ResumeUrl,
                INSERTED.created_at AS CreatedAt,
                INSERTED.updated_at AS UpdatedAt
            VALUES
                (@Id, @OrgId, @FirstName, @LastName, @Email, @Phone, @Source, @ResumeUrl,
                 TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'),
                 TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'));";

        var connection = transaction?.Connection ?? _connectionFactory.CreateConnection();
        var ownsConnection = transaction == null;
        try
        {
            if (ownsConnection)
                connection.Open();
            else if (connection.State != ConnectionState.Open)
                connection.Open();

            return await connection.QuerySingleAsync<CandidateDto>(sql, new
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                dto.FirstName,
                dto.LastName,
                Email = emailLower,
                dto.Phone,
                dto.Source,
                dto.ResumeUrl
            }, transaction);
        }
        finally
        {
            if (ownsConnection)
                connection.Dispose();
        }
    }

    public async Task<CandidateDto?> UpdateAsync(Guid orgId, Guid id, UpdateCandidateRequestDto dto, string emailLower)
    {
        const string sql = @"
            UPDATE candidates
            SET
                first_name = @FirstName,
                last_name = @LastName,
                email = @Email,
                phone = @Phone,
                source = @Source,
                resume_url = @ResumeUrl,
                updated_at = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            WHERE org_id = @OrgId AND id = @Id;

            SELECT
                c.id AS Id,
                c.first_name AS FirstName,
                c.last_name AS LastName,
                c.email AS Email,
                c.phone AS Phone,
                c.source AS Source,
                c.resume_url AS ResumeUrl,
                c.created_at AS CreatedAt,
                c.updated_at AS UpdatedAt
            FROM candidates c
            WHERE c.org_id = @OrgId AND c.id = @Id;";

        using var connection = _connectionFactory.CreateConnection();
        var candidate = await connection.QueryFirstOrDefaultAsync<CandidateDto>(sql, new
        {
            OrgId = orgId,
            Id = id,
            dto.FirstName,
            dto.LastName,
            Email = emailLower,
            dto.Phone,
            dto.Source,
            dto.ResumeUrl
        });
        if (candidate != null)
            await AttachTagsAsync(connection, orgId, new[] { candidate });
        return candidate;
    }

    public async Task<bool> SetResumeUrlAsync(Guid orgId, Guid id, string resumeUrl)
    {
        const string sql = @"
            UPDATE candidates
            SET
                resume_url = @ResumeUrl,
                updated_at = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            WHERE org_id = @OrgId AND id = @Id;";

        using var connection = _connectionFactory.CreateConnection();
        var affected = await connection.ExecuteAsync(sql, new { OrgId = orgId, Id = id, ResumeUrl = resumeUrl });
        return affected > 0;
    }

    public async Task<bool> SetDynamicAnswersJsonAsync(Guid orgId, Guid id, string? dynamicAnswersJson)
    {
        await EnsureTagsSchemaAsync();
        const string sql = @"
            UPDATE candidates
            SET
                dynamic_answers_json = @DynamicAnswersJson,
                updated_at = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            WHERE org_id = @OrgId AND id = @Id;";

        using var connection = _connectionFactory.CreateConnection();
        var affected = await connection.ExecuteAsync(sql, new
        {
            OrgId = orgId,
            Id = id,
            DynamicAnswersJson = dynamicAnswersJson
        });
        return affected > 0;
    }

    public async Task<bool> HasApplicationsAsync(Guid orgId, Guid candidateId)
    {
        const string sql = @"
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM applications a
                WHERE a.org_id = @orgId AND a.candidate_id = @candidateId
            ) THEN 1 ELSE 0 END;";

        using var connection = _connectionFactory.CreateConnection();
        var exists = await connection.ExecuteScalarAsync<int>(sql, new { orgId, candidateId });
        return exists == 1;
    }

    public async Task<bool> DeleteAsync(Guid orgId, Guid id)
    {
        const string sql = @"
            IF OBJECT_ID(N'dbo.candidate_notes', N'U') IS NOT NULL
                DELETE FROM candidate_notes WHERE org_id = @orgId AND candidate_id = @id;
            DELETE FROM candidate_tag_assignments WHERE org_id = @orgId AND candidate_id = @id;
            DELETE FROM candidates WHERE org_id = @orgId AND id = @id;";
        using var connection = _connectionFactory.CreateConnection();
        var affected = await connection.ExecuteAsync(sql, new { orgId, id });
        return affected > 0;
    }

    public async Task EnsureTagsSchemaAsync()
    {
        if (Volatile.Read(ref _tagsSchemaReady) == 1)
            return;

        const string sql = @"
IF OBJECT_ID(N'dbo.candidate_tags', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.candidate_tags
    (
        id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_candidate_tags PRIMARY KEY,
        org_id UNIQUEIDENTIFIER NOT NULL,
        name NVARCHAR(40) NOT NULL,
        normalized_name NVARCHAR(40) NOT NULL,
        created_at DATETIMEOFFSET NOT NULL CONSTRAINT DF_candidate_tags_created DEFAULT (TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')),
        CONSTRAINT UQ_candidate_tags_org_name UNIQUE (org_id, normalized_name)
    );
    CREATE INDEX IX_candidate_tags_org ON dbo.candidate_tags (org_id);
END

IF OBJECT_ID(N'dbo.candidate_tag_assignments', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.candidate_tag_assignments
    (
        org_id UNIQUEIDENTIFIER NOT NULL,
        candidate_id UNIQUEIDENTIFIER NOT NULL,
        tag_id UNIQUEIDENTIFIER NOT NULL,
        created_at DATETIMEOFFSET NOT NULL CONSTRAINT DF_candidate_tag_assignments_created DEFAULT (TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')),
        CONSTRAINT PK_candidate_tag_assignments PRIMARY KEY (candidate_id, tag_id),
        CONSTRAINT FK_candidate_tag_assignments_tag FOREIGN KEY (tag_id) REFERENCES dbo.candidate_tags (id) ON DELETE CASCADE
    );
    CREATE INDEX IX_candidate_tag_assignments_org_candidate ON dbo.candidate_tag_assignments (org_id, candidate_id);
END

IF OBJECT_ID(N'dbo.candidates', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_candidate_tag_assignments_candidate')
   AND EXISTS (
        SELECT 1
        FROM sys.indexes i
        INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.object_id = OBJECT_ID(N'dbo.candidates')
          AND i.is_primary_key = 1
          AND c.name = N'id'
   )
BEGIN
    ALTER TABLE dbo.candidate_tag_assignments
        ADD CONSTRAINT FK_candidate_tag_assignments_candidate
        FOREIGN KEY (candidate_id) REFERENCES dbo.candidates (id) ON DELETE CASCADE;
END

IF COL_LENGTH('dbo.candidates', 'dynamic_answers_json') IS NULL
BEGIN
    ALTER TABLE dbo.candidates ADD dynamic_answers_json NVARCHAR(MAX) NULL;
END";

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(sql);
        Volatile.Write(ref _tagsSchemaReady, 1);
    }

    public async Task<IReadOnlyList<CandidateTagDto>> ListTagsAsync(Guid orgId)
    {
        await EnsureTagsSchemaAsync();
        const string sql = @"
            SELECT id AS Id, name AS Name
            FROM candidate_tags
            WHERE org_id = @orgId
            ORDER BY name;";

        using var connection = _connectionFactory.CreateConnection();
        var tags = await connection.QueryAsync<CandidateTagDto>(sql, new { orgId });
        return tags.ToList();
    }

    public async Task<(CandidateTagDto? Tag, bool CandidateNotFound, string? Error)> AddTagAsync(
        Guid orgId,
        Guid candidateId,
        string name,
        string normalizedName)
    {
        await EnsureTagsSchemaAsync();

        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var tx = connection.BeginTransaction();

        var candidateExists = await connection.ExecuteScalarAsync<int>(
            @"SELECT CASE WHEN EXISTS (
                  SELECT 1 FROM candidates WHERE org_id = @orgId AND id = @candidateId
              ) THEN 1 ELSE 0 END;",
            new { orgId, candidateId },
            tx);
        if (candidateExists != 1)
        {
            tx.Rollback();
            return (null, true, null);
        }

        var tag = await connection.QueryFirstOrDefaultAsync<CandidateTagDto>(
            @"SELECT id AS Id, name AS Name
              FROM candidate_tags
              WHERE org_id = @orgId AND normalized_name = @normalizedName;",
            new { orgId, normalizedName },
            tx);

        if (tag == null)
        {
            try
            {
                tag = await connection.QuerySingleAsync<CandidateTagDto>(
                    @"INSERT INTO candidate_tags (id, org_id, name, normalized_name, created_at)
                      OUTPUT INSERTED.id AS Id, INSERTED.name AS Name
                      VALUES (@id, @orgId, @name, @normalizedName, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'));",
                    new { id = Guid.NewGuid(), orgId, name, normalizedName },
                    tx);
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                tag = await connection.QuerySingleAsync<CandidateTagDto>(
                    @"SELECT id AS Id, name AS Name
                      FROM candidate_tags
                      WHERE org_id = @orgId AND normalized_name = @normalizedName;",
                    new { orgId, normalizedName },
                    tx);
            }
        }

        var alreadyAssigned = await connection.ExecuteScalarAsync<int>(
            @"SELECT CASE WHEN EXISTS (
                  SELECT 1 FROM candidate_tag_assignments
                  WHERE candidate_id = @candidateId AND tag_id = @tagId
              ) THEN 1 ELSE 0 END;",
            new { candidateId, tagId = tag.Id },
            tx);
        if (alreadyAssigned != 1)
        {
            var count = await connection.ExecuteScalarAsync<int>(
                @"SELECT COUNT(1) FROM candidate_tag_assignments
                  WHERE org_id = @orgId AND candidate_id = @candidateId;",
                new { orgId, candidateId },
                tx);
            if (count >= MaxTagsPerCandidate)
            {
                tx.Rollback();
                return (null, false, $"A candidate can have at most {MaxTagsPerCandidate} tags.");
            }

            await connection.ExecuteAsync(
                @"INSERT INTO candidate_tag_assignments (org_id, candidate_id, tag_id, created_at)
                  VALUES (@orgId, @candidateId, @tagId, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'));",
                new { orgId, candidateId, tagId = tag.Id },
                tx);
        }

        tx.Commit();
        return (tag, false, null);
    }

    public async Task<bool> RemoveTagAsync(Guid orgId, Guid candidateId, Guid tagId)
    {
        await EnsureTagsSchemaAsync();

        using var connection = _connectionFactory.CreateConnection();
        var candidateExists = await connection.ExecuteScalarAsync<int>(
            @"SELECT CASE WHEN EXISTS (
                  SELECT 1 FROM candidates WHERE org_id = @orgId AND id = @candidateId
              ) THEN 1 ELSE 0 END;",
            new { orgId, candidateId });
        if (candidateExists != 1)
            return false;

        await connection.ExecuteAsync(
            @"DELETE FROM candidate_tag_assignments
              WHERE org_id = @orgId AND candidate_id = @candidateId AND tag_id = @tagId;",
            new { orgId, candidateId, tagId });
        return true;
    }

    public async Task EnsureNotesSchemaAsync()
    {
        if (Volatile.Read(ref _notesSchemaReady) == 1)
            return;

        const string sql = @"
IF OBJECT_ID(N'dbo.candidate_notes', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.candidate_notes
    (
        id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_candidate_notes PRIMARY KEY,
        org_id UNIQUEIDENTIFIER NOT NULL,
        candidate_id UNIQUEIDENTIFIER NOT NULL,
        body NVARCHAR(4000) NOT NULL,
        created_by_name NVARCHAR(400) NULL,
        created_by_email NVARCHAR(640) NULL,
        created_at DATETIMEOFFSET NOT NULL CONSTRAINT DF_candidate_notes_created DEFAULT (TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'))
    );
    CREATE INDEX IX_candidate_notes_org_candidate ON dbo.candidate_notes (org_id, candidate_id, created_at DESC);
END

IF OBJECT_ID(N'dbo.candidates', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_candidate_notes_candidate')
   AND EXISTS (
        SELECT 1
        FROM sys.indexes i
        INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.object_id = OBJECT_ID(N'dbo.candidates')
          AND i.is_primary_key = 1
          AND c.name = N'id'
   )
BEGIN
    ALTER TABLE dbo.candidate_notes
        ADD CONSTRAINT FK_candidate_notes_candidate
        FOREIGN KEY (candidate_id) REFERENCES dbo.candidates (id) ON DELETE CASCADE;
END";

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(sql);
        Volatile.Write(ref _notesSchemaReady, 1);
    }

    public async Task<IReadOnlyList<CandidateNoteDto>> ListNotesAsync(Guid orgId, Guid candidateId)
    {
        await EnsureNotesSchemaAsync();
        const string sql = @"
            SELECT
                id AS Id,
                body AS Body,
                created_by_name AS CreatedByName,
                created_by_email AS CreatedByEmail,
                created_at AS CreatedAt
            FROM candidate_notes
            WHERE org_id = @orgId AND candidate_id = @candidateId
            ORDER BY created_at DESC, id DESC;";

        using var connection = _connectionFactory.CreateConnection();
        var notes = await connection.QueryAsync<CandidateNoteDto>(sql, new { orgId, candidateId });
        return notes.ToList();
    }

    public async Task<(CandidateNoteDto? Note, bool CandidateNotFound)> AddNoteAsync(
        Guid orgId,
        Guid candidateId,
        string body,
        string? createdByName,
        string? createdByEmail)
    {
        await EnsureNotesSchemaAsync();

        using var connection = _connectionFactory.CreateConnection();
        var candidateExists = await connection.ExecuteScalarAsync<int>(
            @"SELECT CASE WHEN EXISTS (
                  SELECT 1 FROM candidates WHERE org_id = @orgId AND id = @candidateId
              ) THEN 1 ELSE 0 END;",
            new { orgId, candidateId });
        if (candidateExists != 1)
            return (null, true);

        var note = await connection.QuerySingleAsync<CandidateNoteDto>(
            @"INSERT INTO candidate_notes (id, org_id, candidate_id, body, created_by_name, created_by_email, created_at)
              OUTPUT
                  INSERTED.id AS Id,
                  INSERTED.body AS Body,
                  INSERTED.created_by_name AS CreatedByName,
                  INSERTED.created_by_email AS CreatedByEmail,
                  INSERTED.created_at AS CreatedAt
              VALUES (@id, @orgId, @candidateId, @body, @createdByName, @createdByEmail, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'));",
            new
            {
                id = Guid.NewGuid(),
                orgId,
                candidateId,
                body,
                createdByName,
                createdByEmail
            });

        return (note, false);
    }

    public async Task<bool> DeleteNoteAsync(Guid orgId, Guid candidateId, Guid noteId)
    {
        await EnsureNotesSchemaAsync();

        using var connection = _connectionFactory.CreateConnection();
        var candidateExists = await connection.ExecuteScalarAsync<int>(
            @"SELECT CASE WHEN EXISTS (
                  SELECT 1 FROM candidates WHERE org_id = @orgId AND id = @candidateId
              ) THEN 1 ELSE 0 END;",
            new { orgId, candidateId });
        if (candidateExists != 1)
            return false;

        await connection.ExecuteAsync(
            @"DELETE FROM candidate_notes
              WHERE org_id = @orgId AND candidate_id = @candidateId AND id = @noteId;",
            new { orgId, candidateId, noteId });
        return true;
    }

    private static async Task AttachTagsAsync<T>(IDbConnection connection, Guid orgId, IReadOnlyList<T> candidates)
        where T : CandidateDto
    {
        if (candidates.Count == 0)
            return;

        const string sql = @"
            SELECT
                a.candidate_id AS CandidateId,
                t.id AS Id,
                t.name AS Name
            FROM candidate_tag_assignments a
            INNER JOIN candidate_tags t ON t.id = a.tag_id AND t.org_id = a.org_id
            WHERE a.org_id = @orgId
              AND a.candidate_id IN @ids
            ORDER BY t.name;";

        var rows = (await connection.QueryAsync<CandidateTagLinkRow>(sql, new
        {
            orgId,
            ids = candidates.Select(c => c.Id).ToArray()
        })).ToList();

        var byCandidate = rows
            .GroupBy(r => r.CandidateId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => new CandidateTagDto { Id = r.Id, Name = r.Name }).ToList());

        foreach (var candidate in candidates)
            candidate.Tags = byCandidate.TryGetValue(candidate.Id, out var tags) ? tags : new List<CandidateTagDto>();
    }

    private sealed class CandidateTagLinkRow
    {
        public Guid CandidateId { get; set; }
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}

