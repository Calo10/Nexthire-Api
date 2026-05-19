using System.Data;
using Dapper;
using nexthire_api.Data;
using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public class CandidateRepository : ICandidateRepository
{
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
        int page,
        int pageSize,
        string sort,
        string dir)
    {
        var trimmedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var trimmedSource = string.IsNullOrWhiteSpace(source) ? null : source.Trim();

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

        var offset = (page - 1) * pageSize;

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
                    OR LOWER(c.email) LIKE '%' + LOWER(@search) + '%'
                  );

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
                    OR LOWER(c.email) LIKE '%' + LOWER(@search) + '%'
                  )
            ORDER BY c.created_at {orderDir}, c.id {orderDir}
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;";

        using var connection = _connectionFactory.CreateConnection();
        using var multi = await connection.QueryMultipleAsync(sql, new
        {
            orgId,
            search = trimmedSearch,
            source = trimmedSource,
            from,
            to,
            offset,
            pageSize
        });

        var total = await multi.ReadSingleAsync<int>();
        var items = (await multi.ReadAsync<CandidateListItemDto>()).ToList();

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
                c.created_at AS CreatedAt,
                c.updated_at AS UpdatedAt
            FROM candidates c
            WHERE c.org_id = @orgId AND c.id = @id;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<CandidateDto>(sql, new { orgId, id });
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
        return await connection.QueryFirstOrDefaultAsync<CandidateDto>(sql, new
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
        const string sql = @"DELETE FROM candidates WHERE org_id = @orgId AND id = @id;";
        using var connection = _connectionFactory.CreateConnection();
        var affected = await connection.ExecuteAsync(sql, new { orgId, id });
        return affected > 0;
    }
}

