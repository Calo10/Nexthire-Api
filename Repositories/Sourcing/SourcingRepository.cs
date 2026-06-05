using System.Collections.Concurrent;
using System.Data;
using System.Data.SqlClient;
using Dapper;
using nexthire_api.Data;
using nexthire_api.DTOs;
using nexthire_api.DTOs.Sourcing;
using nexthire_api.Sourcing;

namespace nexthire_api.Repositories.Sourcing;

/// <summary>
/// Sourcing tables use org-scoped rows where noted. Assumed columns match dbo.sourcing_* DDL (snake_case).
/// Campaign metrics: expects dbo.sourcing_campaign_metrics.spend joined via campaign_id to sourcing_campaigns.
/// </summary>
public class SourcingRepository : ISourcingRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<SourcingRepository> _logger;

    private static readonly ConcurrentDictionary<string, HashSet<string>> TrackingEventColumnsCache = new();
    private static readonly ConcurrentDictionary<string, HashSet<string>> LeadEventColumnsCache = new();
    private static readonly ConcurrentDictionary<string, HashSet<string>> SourceTypeColumnsCache = new();

    public SourcingRepository(IDbConnectionFactory connectionFactory, ILogger<SourcingRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<SourcingDashboardDto> GetDashboardAsync(Guid orgId)
    {
        const string sql = @"
            DECLARE @today date = CAST(SYSUTCDATETIME() AS date);

            SELECT 
              (SELECT COUNT(1)
               FROM sourcing_leads sl
               WHERE sl.org_id = @orgId
                 AND sl.converted_at IS NOT NULL
                 AND CAST(sl.converted_at AS date) = @today) AS CandidatesCapturedToday,
              (SELECT COUNT(1) FROM sourcing_leads sl WHERE sl.org_id = @orgId) AS TotalLeads,
              (SELECT COUNT(1) FROM sourcing_campaigns sc WHERE sc.org_id = @orgId AND sc.status = N'active') AS ActiveCampaigns,
              (SELECT COALESCE(SUM(CAST(m.spend AS decimal(18,4))), 0)
               FROM sourcing_campaign_metrics m
               INNER JOIN sourcing_campaigns c ON c.id = m.campaign_id AND c.org_id = @orgId) AS TotalSpend,
              (SELECT COUNT(1)
               FROM sourcing_leads sl
               WHERE sl.org_id = @orgId AND sl.status = N'converted') AS ConvertedLeads;

            SELECT sl.source_type_code AS SourceTypeCode,
                   COALESCE(st.name, sl.source_type_code) AS SourceTypeName,
                   COUNT(1) AS Count
            FROM sourcing_leads sl
            LEFT JOIN sourcing_source_types st ON st.code = sl.source_type_code
            WHERE sl.org_id = @orgId
            GROUP BY sl.source_type_code, st.name
            ORDER BY COUNT(1) DESC;

            SELECT TOP 10
                   sl.id AS Id,
                   sl.full_name AS FullName,
                   sl.email AS Email,
                   sl.status AS Status,
                   sl.source_type_code AS SourceTypeCode,
                   sl.created_at AS CreatedAt
            FROM sourcing_leads sl
            WHERE sl.org_id = @orgId
            ORDER BY sl.created_at DESC;";

        using var connection = _connectionFactory.CreateConnection();
        using var multi = await connection.QueryMultipleAsync(sql, new { orgId });

        var head = await multi.ReadFirstOrDefaultAsync<DashboardHeadRow>();
        var bySource = (await multi.ReadAsync<LeadCountBySourceDto>()).ToList();
        var recent = (await multi.ReadAsync<SourcingLeadSummaryDto>()).ToList();

        decimal? costPerCandidate = null;
        decimal? conversionRate = null;

        if (head != null)
        {
            if (head.ConvertedLeads > 0)
                costPerCandidate = head.TotalSpend / head.ConvertedLeads;

            if (head.TotalLeads > 0)
                conversionRate = Math.Round(100m * head.ConvertedLeads / head.TotalLeads, 2, MidpointRounding.AwayFromZero);
        }

        return new SourcingDashboardDto
        {
            CandidatesCapturedToday = head?.CandidatesCapturedToday ?? 0,
            TotalLeads = head?.TotalLeads ?? 0,
            ActiveCampaigns = head?.ActiveCampaigns ?? 0,
            CostPerCandidate = costPerCandidate,
            ConversionRate = conversionRate,
            LeadsBySource = bySource,
            RecentLeads = recent
        };
    }

    private sealed class DashboardHeadRow
    {
        public int CandidatesCapturedToday { get; set; }
        public int TotalLeads { get; set; }
        public int ActiveCampaigns { get; set; }
        public decimal TotalSpend { get; set; }
        public int ConvertedLeads { get; set; }
    }

    public async Task<PagedResult<SourcingLeadListItemDto>> GetLeadsPagedAsync(
        Guid orgId,
        Guid? jobId,
        Guid? campaignId,
        string? sourceTypeCode,
        string? status,
        string? search,
        string? availability,
        decimal? minFitScore,
        int page,
        int pageSize,
        string sort,
        string dir)
    {
        var offset = (page - 1) * pageSize;
        var sortCol = MapLeadSortColumn(sort);
        var orderDir = NormalizeDir(dir);

        var sql = $@"
            SELECT COUNT(1)
            FROM sourcing_leads sl
            WHERE sl.org_id = @orgId
              AND (@jobId IS NULL OR sl.job_id = @jobId)
              AND (@campaignId IS NULL OR sl.campaign_id = @campaignId)
              AND (@sourceTypeCode IS NULL OR sl.source_type_code = @sourceTypeCode)
              AND (@status IS NULL OR sl.status = @status)
              AND (@minFitScore IS NULL OR (sl.fit_score IS NOT NULL AND sl.fit_score >= @minFitScore))
              AND (
                    @search IS NULL
                    OR LOWER(ISNULL(sl.full_name, '')) LIKE '%' + LOWER(@search) + '%'
                    OR LOWER(ISNULL(sl.email, '')) LIKE '%' + LOWER(@search) + '%'
                    OR LOWER(ISNULL(sl.phone, '')) LIKE '%' + LOWER(@search) + '%'
                    OR LOWER(ISNULL(sl.first_name, '')) LIKE '%' + LOWER(@search) + '%'
                    OR LOWER(ISNULL(sl.last_name, '')) LIKE '%' + LOWER(@search) + '%'
                  );

            SELECT
                sl.id AS Id,
                sl.campaign_id AS CampaignId,
                sl.job_id AS JobId,
                sl.source_type_code AS SourceTypeCode,
                sl.first_name AS FirstName,
                sl.last_name AS LastName,
                sl.full_name AS FullName,
                sl.email AS Email,
                sl.phone AS Phone,
                sl.resume_url AS ResumeUrl,
                sl.status AS Status,
                sl.fit_score AS FitScore,
                sl.created_at AS CreatedAt,
                sl.updated_at AS UpdatedAt
            FROM sourcing_leads sl
            WHERE sl.org_id = @orgId
              AND (@jobId IS NULL OR sl.job_id = @jobId)
              AND (@campaignId IS NULL OR sl.campaign_id = @campaignId)
              AND (@sourceTypeCode IS NULL OR sl.source_type_code = @sourceTypeCode)
              AND (@status IS NULL OR sl.status = @status)
              AND (@minFitScore IS NULL OR (sl.fit_score IS NOT NULL AND sl.fit_score >= @minFitScore))
              AND (
                    @search IS NULL
                    OR LOWER(ISNULL(sl.full_name, '')) LIKE '%' + LOWER(@search) + '%'
                    OR LOWER(ISNULL(sl.email, '')) LIKE '%' + LOWER(@search) + '%'
                    OR LOWER(ISNULL(sl.phone, '')) LIKE '%' + LOWER(@search) + '%'
                    OR LOWER(ISNULL(sl.first_name, '')) LIKE '%' + LOWER(@search) + '%'
                    OR LOWER(ISNULL(sl.last_name, '')) LIKE '%' + LOWER(@search) + '%'
                  )
            ORDER BY {sortCol} {orderDir}, sl.id {orderDir}
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;";

        using var connection = _connectionFactory.CreateConnection();
        using var multi = await connection.QueryMultipleAsync(sql, new
        {
            orgId,
            jobId,
            campaignId,
            sourceTypeCode,
            status,
            search,
            availability,
            minFitScore,
            offset,
            pageSize
        });

        var total = await multi.ReadSingleAsync<int>();
        var items = (await multi.ReadAsync<SourcingLeadListItemDto>()).ToList();

        return new PagedResult<SourcingLeadListItemDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            Total = total
        };
    }

    private static string MapLeadSortColumn(string sort)
    {
        var s = string.IsNullOrWhiteSpace(sort) ? "created_at" : sort.Trim().ToLowerInvariant();
        return s switch
        {
            "created_at" => "sl.created_at",
            "updated_at" => "sl.updated_at",
            "fit_score" => "sl.fit_score",
            "full_name" => "sl.full_name",
            "status" => "sl.status",
            _ => throw new ArgumentException("Unsupported sort field.", nameof(sort))
        };
    }

    private static string NormalizeDir(string dir)
    {
        var d = string.IsNullOrWhiteSpace(dir) ? "desc" : dir.Trim().ToLowerInvariant();
        return d switch
        {
            "asc" => "ASC",
            "desc" => "DESC",
            _ => throw new ArgumentException("Unsupported sort direction.", nameof(dir))
        };
    }

    public async Task<SourcingLeadDetailDto?> GetLeadByIdAsync(Guid orgId, Guid leadId)
    {
        const string sql = @"
            SELECT
                sl.id AS Id,
                sl.campaign_id AS CampaignId,
                sl.job_id AS JobId,
                sl.source_type_code AS SourceTypeCode,
                sl.first_name AS FirstName,
                sl.last_name AS LastName,
                sl.full_name AS FullName,
                sl.email AS Email,
                sl.phone AS Phone,
                sl.resume_url AS ResumeUrl,
                sl.status AS Status,
                sl.fit_score AS FitScore,
                sl.created_at AS CreatedAt,
                sl.updated_at AS UpdatedAt,
                sl.qualification_notes AS QualificationNotes,
                sl.dynamic_answers_json AS DynamicAnswersJson,
                sl.raw_payload_json AS RawPayloadJson,
                sl.contacted_at AS ContactedAt,
                sl.converted_candidate_id AS ConvertedCandidateId,
                sl.converted_application_id AS ConvertedApplicationId,
                sl.converted_at AS ConvertedAt,
                sc.id AS CampaignRefId,
                sc.name AS CampaignName,
                j.id AS JobRefId,
                j.title AS JobRefTitle,
                st.name AS SourceTypeLookupName,
                st.name AS SourceTypeLookupDisplayName
            FROM sourcing_leads sl
            LEFT JOIN sourcing_campaigns sc ON sc.id = sl.campaign_id AND sc.org_id = @orgId
            LEFT JOIN jobs j ON j.id = sl.job_id AND j.org_id = @orgId
            LEFT JOIN sourcing_source_types st ON st.code = sl.source_type_code
            WHERE sl.org_id = @orgId AND sl.id = @leadId;";

        using var connection = _connectionFactory.CreateConnection();
        var row = await connection.QueryFirstOrDefaultAsync<LeadDetailFlatRow>(sql, new { orgId, leadId });
        return row == null ? null : ToLeadDetailDto(row);
    }

    private sealed class LeadDetailFlatRow
    {
        public Guid Id { get; set; }
        public Guid? CampaignId { get; set; }
        public Guid? JobId { get; set; }
        public string SourceTypeCode { get; set; } = string.Empty;
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string? FullName { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string? ResumeUrl { get; set; }
        public string Status { get; set; } = string.Empty;
        public decimal? FitScore { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public string? QualificationNotes { get; set; }
        public string? DynamicAnswersJson { get; set; }
        public string? RawPayloadJson { get; set; }
        public DateTimeOffset? ContactedAt { get; set; }
        public Guid? ConvertedCandidateId { get; set; }
        public Guid? ConvertedApplicationId { get; set; }
        public DateTimeOffset? ConvertedAt { get; set; }
        public Guid? CampaignRefId { get; set; }
        public string? CampaignName { get; set; }
        public Guid? JobRefId { get; set; }
        public string? JobRefTitle { get; set; }
        public string? SourceTypeLookupName { get; set; }
        public string? SourceTypeLookupDisplayName { get; set; }
    }

    private static SourcingLeadDetailDto ToLeadDetailDto(LeadDetailFlatRow r)
    {
        return new SourcingLeadDetailDto
        {
            Id = r.Id,
            CampaignId = r.CampaignId,
            JobId = r.JobId,
            SourceTypeCode = r.SourceTypeCode,
            FirstName = r.FirstName,
            LastName = r.LastName,
            FullName = r.FullName,
            Email = r.Email,
            Phone = r.Phone,
            ResumeUrl = r.ResumeUrl,
            Status = r.Status,
            FitScore = r.FitScore,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt,
            QualificationNotes = r.QualificationNotes,
            DynamicAnswersJson = r.DynamicAnswersJson,
            RawPayloadJson = r.RawPayloadJson,
            ContactedAt = r.ContactedAt,
            ConvertedCandidateId = r.ConvertedCandidateId,
            ConvertedApplicationId = r.ConvertedApplicationId,
            ConvertedAt = r.ConvertedAt,
            Campaign = r.CampaignRefId.HasValue
                ? new SourcingContextDto { Id = r.CampaignRefId.Value, Name = r.CampaignName }
                : null,
            Job = r.JobRefId.HasValue
                ? new SourcingContextDto { Id = r.JobRefId.Value, Title = r.JobRefTitle }
                : null,
            SourceType = !string.IsNullOrWhiteSpace(r.SourceTypeCode)
                ? new SourcingSourceTypeSummaryDto
                {
                    Code = r.SourceTypeCode,
                    Name = r.SourceTypeLookupName,
                    DisplayName = r.SourceTypeLookupDisplayName
                }
                : null
        };
    }

    public async Task<SourcingLeadDetailDto> CreateLeadAsync(Guid orgId, CreateSourcingLeadRequestDto dto)
    {
        var id = Guid.NewGuid();

        const string sql = @"
            INSERT INTO sourcing_leads (
                id, org_id, campaign_id, job_id, source_type_code,
                first_name, last_name, full_name, email, phone,
                resume_url, dynamic_answers_json,
                fit_score, qualification_notes, raw_payload_json,
                status, created_at, updated_at
            )
            VALUES (
                @id, @orgId, @campaignId, @jobId, @sourceTypeCode,
                @firstName, @lastName, @fullName, @email, @phone,
                @resumeUrl, @dynamicAnswersJson,
                @fitScore, @qualificationNotes, @rawPayloadJson,
                @status, SYSUTCDATETIME(), SYSUTCDATETIME()
            );";

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(sql, new
        {
            id,
            orgId,
            dto.CampaignId,
            dto.JobId,
            dto.SourceTypeCode,
            dto.FirstName,
            dto.LastName,
            dto.FullName,
            dto.Email,
            dto.Phone,
            dto.ResumeUrl,
            dto.DynamicAnswersJson,
            dto.FitScore,
            dto.QualificationNotes,
            dto.RawPayloadJson,
            status = SourcingConstants.DefaultLeadStatus
        });

        await InsertLeadEventAsync(orgId, id, "created", notes: null, transaction: null);

        var detail = await GetLeadByIdAsync(orgId, id);
        return detail ?? throw new InvalidOperationException("Lead was created but could not be reloaded.");
    }

    public async Task<SourcingLeadDetailDto?> UpdateLeadAsync(Guid orgId, Guid leadId, UpdateSourcingLeadRequestDto dto)
    {
        const string sql = @"
            UPDATE sourcing_leads
            SET
                campaign_id = @campaignId,
                job_id = @jobId,
                source_type_code = COALESCE(@sourceTypeCode, source_type_code),
                first_name = @firstName,
                last_name = @lastName,
                full_name = @fullName,
                email = @email,
                phone = @phone,
                resume_url = @resumeUrl,
                dynamic_answers_json = @dynamicAnswersJson,
                fit_score = @fitScore,
                qualification_notes = @qualificationNotes,
                raw_payload_json = @rawPayloadJson,
                updated_at = SYSUTCDATETIME()
            WHERE org_id = @orgId AND id = @leadId;";

        using var connection = _connectionFactory.CreateConnection();
        var n = await connection.ExecuteAsync(sql, new
        {
            orgId,
            leadId,
            dto.CampaignId,
            dto.JobId,
            dto.SourceTypeCode,
            dto.FirstName,
            dto.LastName,
            dto.FullName,
            dto.Email,
            dto.Phone,
            dto.ResumeUrl,
            dto.DynamicAnswersJson,
            dto.FitScore,
            dto.QualificationNotes,
            dto.RawPayloadJson
        });

        if (n == 0)
            return null;

        return await GetLeadByIdAsync(orgId, leadId);
    }

    public async Task<SourcingLeadDetailDto?> PatchLeadStatusAsync(Guid orgId, Guid leadId, string newStatus, string? notes)
    {
        const string updateSql = @"
            UPDATE sourcing_leads
            SET
                status = @newStatus,
                contacted_at = CASE
                    WHEN @newStatus = N'contacted' AND contacted_at IS NULL
                    THEN TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
                    ELSE contacted_at
                END,
                updated_at = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            WHERE org_id = @orgId AND id = @leadId;";

        using var connection = _connectionFactory.CreateConnection();
        var updated = await connection.ExecuteAsync(updateSql, new { orgId, leadId, newStatus });
        if (updated == 0)
            return null;

        await InsertLeadEventAsync(orgId, leadId, newStatus, notes, transaction: null);

        return await GetLeadByIdAsync(orgId, leadId);
    }

    public Task<bool> SourceTypeExistsAsync(string code)
    {
        const string sql = @"
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM sourcing_source_types WHERE code = @code
            ) THEN 1 ELSE 0 END;";
        return ExistsAsync(sql, new { code });
    }

    private async Task<bool> ExistsAsync(string sql, object param)
    {
        using var connection = _connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<int>(sql, param) == 1;
    }

    public async Task<IReadOnlyList<SourcingSourceTypeDto>> GetActiveSourceTypesAsync()
    {
        using var connection = _connectionFactory.CreateConnection();
        var cols = await GetSourceTypeColumnSetAsync(connection);
        var hasDescription = cols.Contains("description");
        var hasSortOrder = cols.Contains("sort_order");

        var sql = $@"
            SELECT
                code AS Code,
                name AS Name,
                name AS DisplayName,
                {(hasDescription ? "description" : "CAST(NULL AS nvarchar(max))")} AS Description,
                CAST(is_active AS bit) AS IsActive,
                {(hasSortOrder ? "sort_order" : "CAST(0 AS int)")} AS SortOrder
            FROM sourcing_source_types
            WHERE is_active = 1
            ORDER BY {(hasSortOrder ? "sort_order ASC," : string.Empty)} name ASC;";

        var rows = await connection.QueryAsync<SourcingSourceTypeDto>(sql);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<SourcingSourceConnectionListItemDto>> GetSourceConnectionsAsync(Guid orgId)
    {
        using var connection = _connectionFactory.CreateConnection();
        var cols = await GetSourceTypeColumnSetAsync(connection);
        var hasSortOrder = cols.Contains("sort_order");

        var sql = $@"
            SELECT
                st.code AS SourceTypeCode,
                st.name AS SourceTypeName,
                st.name AS DisplayName,
                CAST(COALESCE(conn.is_connected, 0) AS bit) AS IsConnected,
                CAST(COALESCE(conn.is_active, 0) AS bit) AS IsActive,
                conn.config_json AS ConfigJson,
                conn.updated_at AS UpdatedAt
            FROM sourcing_source_types st
            LEFT JOIN sourcing_source_connections conn
                ON conn.source_type_code = st.code AND conn.org_id = @orgId
            WHERE st.is_active = 1
            ORDER BY {(hasSortOrder ? "st.sort_order ASC," : string.Empty)} st.name ASC;";

        var rows = await connection.QueryAsync<SourcingSourceConnectionListItemDto>(sql, new { orgId });
        return rows.ToList();
    }

    public async Task UpsertSourceConnectionAsync(Guid orgId, UpsertSourcingSourceConnectionRequestDto dto)
    {
        const string sql = @"
            IF EXISTS (
                SELECT 1 FROM sourcing_source_connections
                WHERE org_id = @orgId AND source_type_code = @code
            )
            BEGIN
                UPDATE sourcing_source_connections
                SET is_connected = @isConnected,
                    is_active = @isActive,
                    config_json = @configJson,
                    updated_at = SYSUTCDATETIME()
                WHERE org_id = @orgId AND source_type_code = @code;
            END
            ELSE
            BEGIN
                INSERT INTO sourcing_source_connections (id, org_id, source_type_code, is_connected, is_active, config_json, created_at, updated_at)
                VALUES (@id, @orgId, @code, @isConnected, @isActive, @configJson, SYSUTCDATETIME(), SYSUTCDATETIME());
            END";

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(sql, new
        {
            id = Guid.NewGuid(),
            orgId,
            code = dto.SourceTypeCode,
            dto.IsConnected,
            dto.IsActive,
            configJson = dto.ConfigJson
        });
    }

    public async Task<string?> GetActiveSourceConnectionConfigJsonAsync(
        Guid orgId,
        string sourceTypeCode,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
            SELECT config_json
            FROM sourcing_source_connections
            WHERE org_id = @orgId
              AND source_type_code = @sourceTypeCode
              AND is_connected = 1
              AND is_active = 1
              AND config_json IS NOT NULL
              AND LTRIM(RTRIM(config_json)) <> '';";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<string?>(
            new CommandDefinition(sql, new { orgId, sourceTypeCode }, cancellationToken: cancellationToken));
    }

    public async Task<PagedResult<SourcingCampaignListItemDto>> GetCampaignsPagedAsync(
        Guid orgId,
        Guid? jobId,
        string? platform,
        string? status,
        string? search,
        int page,
        int pageSize)
    {
        var offset = (page - 1) * pageSize;
        const string sql = @"
            SELECT COUNT(1)
            FROM sourcing_campaigns sc
            LEFT JOIN jobs j ON j.id = sc.job_id AND j.org_id = @orgId
            WHERE sc.org_id = @orgId
              AND (@jobId IS NULL OR sc.job_id = @jobId)
              AND (@platform IS NULL OR sc.platform = @platform)
              AND (@status IS NULL OR sc.status = @status)
              AND (
                    @search IS NULL
                    OR LOWER(sc.name) LIKE '%' + LOWER(@search) + '%'
                    OR LOWER(sc.platform) LIKE '%' + LOWER(@search) + '%'
                  );

            SELECT
                sc.id AS Id,
                sc.job_id AS JobId,
                j.title AS JobTitle,
                sc.name AS Name,
                sc.platform AS Platform,
                sc.status AS Status,
                sc.daily_budget AS DailyBudget,
                sc.total_budget AS TotalBudget,
                sc.currency AS Currency,
                sc.start_date AS StartDate,
                sc.end_date AS EndDate,
                sc.created_at AS CreatedAt,
                sc.updated_at AS UpdatedAt
            FROM sourcing_campaigns sc
            LEFT JOIN jobs j ON j.id = sc.job_id AND j.org_id = @orgId
            WHERE sc.org_id = @orgId
              AND (@jobId IS NULL OR sc.job_id = @jobId)
              AND (@platform IS NULL OR sc.platform = @platform)
              AND (@status IS NULL OR sc.status = @status)
              AND (
                    @search IS NULL
                    OR LOWER(sc.name) LIKE '%' + LOWER(@search) + '%'
                    OR LOWER(sc.platform) LIKE '%' + LOWER(@search) + '%'
                  )
            ORDER BY sc.created_at DESC, sc.id DESC
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;";

        using var connection = _connectionFactory.CreateConnection();
        using var multi = await connection.QueryMultipleAsync(sql, new { orgId, jobId, platform, status, search, offset, pageSize });

        var total = await multi.ReadSingleAsync<int>();
        var items = (await multi.ReadAsync<SourcingCampaignListItemDto>()).ToList();

        return new PagedResult<SourcingCampaignListItemDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            Total = total
        };
    }

    public async Task EnsureCampaignsSchemaAsync(CancellationToken cancellationToken = default)
    {
        const string sql = @"
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'sourcing_campaigns' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.sourcing_campaigns
    (
        id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_sourcing_campaigns PRIMARY KEY,
        org_id UNIQUEIDENTIFIER NOT NULL,
        job_id UNIQUEIDENTIFIER NULL,
        name NVARCHAR(240) NOT NULL,
        platform NVARCHAR(80) NOT NULL,
        status NVARCHAR(40) NOT NULL CONSTRAINT DF_sourcing_campaigns_status DEFAULT ('draft'),
        daily_budget DECIMAL(18, 2) NULL,
        total_budget DECIMAL(18, 2) NULL,
        currency NVARCHAR(8) NOT NULL CONSTRAINT DF_sourcing_campaigns_currency DEFAULT ('USD'),
        start_date DATETIMEOFFSET NULL,
        end_date DATETIMEOFFSET NULL,
        landing_page_url NVARCHAR(2000) NULL,
        tracking_code NVARCHAR(120) NULL,
        external_campaign_id NVARCHAR(240) NULL,
        external_ad_account_id NVARCHAR(240) NULL,
        created_at DATETIMEOFFSET NOT NULL CONSTRAINT DF_sourcing_campaigns_created DEFAULT (SYSUTCDATETIME()),
        updated_at DATETIMEOFFSET NOT NULL CONSTRAINT DF_sourcing_campaigns_updated DEFAULT (SYSUTCDATETIME())
    );
END";

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    public async Task EnsureDefaultSourceTypesAsync(CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        var cols = await GetSourceTypeColumnSetAsync(connection);
        if (!cols.Contains("code"))
            return;

        var seeds = new (string Code, string Name, string? Description)[]
        {
            ("public_apply", "Public apply", "Applications from the public careers page"),
            ("whatsapp", "WhatsApp", "Applications via WhatsApp bot"),
            ("meta_ads", "Meta Ads", "Applications from Meta advertising"),
        };

        foreach (var (code, name, description) in seeds)
            await InsertSourceTypeIfMissingAsync(connection, cols, code, name, description, cancellationToken);
    }

    private async Task InsertSourceTypeIfMissingAsync(
        IDbConnection connection,
        HashSet<string> cols,
        string code,
        string name,
        string? description,
        CancellationToken cancellationToken)
    {
        var exists = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(1) FROM dbo.sourcing_source_types WHERE code = @code",
                new { code },
                cancellationToken: cancellationToken));
        if (exists > 0)
            return;

        var insertCols = new List<string> { "code" };
        var insertVals = new List<string> { "@code" };
        var param = new DynamicParameters();
        param.Add("code", code);

        if (cols.Contains("id"))
        {
            insertCols.Add("id");
            insertVals.Add("@id");
            param.Add("id", Guid.NewGuid());
        }

        if (cols.Contains("name"))
        {
            insertCols.Add("name");
            insertVals.Add("@name");
            param.Add("name", name);
        }

        if (cols.Contains("display_name"))
        {
            insertCols.Add("display_name");
            insertVals.Add("@displayName");
            param.Add("displayName", name);
        }

        if (cols.Contains("description") && !string.IsNullOrWhiteSpace(description))
        {
            insertCols.Add("description");
            insertVals.Add("@description");
            param.Add("description", description);
        }

        if (cols.Contains("is_active"))
        {
            insertCols.Add("is_active");
            insertVals.Add("@isActive");
            param.Add("isActive", true);
        }

        if (cols.Contains("sort_order"))
        {
            insertCols.Add("sort_order");
            insertVals.Add("@sortOrder");
            param.Add("sortOrder", 0);
        }

        if (cols.Contains("created_at"))
        {
            insertCols.Add("created_at");
            insertVals.Add("SYSUTCDATETIME()");
        }

        if (cols.Contains("updated_at"))
        {
            insertCols.Add("updated_at");
            insertVals.Add("SYSUTCDATETIME()");
        }

        var sql =
            $"INSERT INTO dbo.sourcing_source_types ({string.Join(", ", insertCols)}) VALUES ({string.Join(", ", insertVals)})";
        await connection.ExecuteAsync(new CommandDefinition(sql, param, cancellationToken: cancellationToken));
        _logger.LogInformation("Seeded sourcing_source_types code={Code}", code);
    }

    public async Task<SourcingCampaignDetailDto> UpsertMetaAdsCampaignAsync(
        Guid orgId,
        Guid campaignId,
        Guid jobId,
        string name,
        string sourcingStatus,
        decimal? dailyBudget,
        string? landingPageUrl,
        string metaCampaignId,
        string adAccountId,
        CancellationToken cancellationToken = default)
    {
        await EnsureCampaignsSchemaAsync(cancellationToken);

        const string sql = @"
MERGE dbo.sourcing_campaigns AS target
USING (SELECT @campaignId AS id) AS source
ON target.id = source.id
WHEN MATCHED THEN
    UPDATE SET
        org_id = @orgId,
        job_id = @jobId,
        name = @name,
        platform = N'meta',
        status = @status,
        daily_budget = @dailyBudget,
        landing_page_url = @landingPageUrl,
        external_campaign_id = @metaCampaignId,
        external_ad_account_id = @adAccountId,
        updated_at = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (
        id, org_id, job_id, name, platform, status,
        daily_budget, currency, landing_page_url,
        external_campaign_id, external_ad_account_id,
        created_at, updated_at
    )
    VALUES (
        @campaignId, @orgId, @jobId, @name, N'meta', @status,
        @dailyBudget, N'USD', @landingPageUrl,
        @metaCampaignId, @adAccountId,
        SYSUTCDATETIME(), SYSUTCDATETIME()
    );";

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                campaignId,
                orgId,
                jobId,
                name,
                status = sourcingStatus,
                dailyBudget,
                landingPageUrl,
                metaCampaignId,
                adAccountId
            },
            cancellationToken: cancellationToken));

        var detail = await GetCampaignByIdAsync(orgId, campaignId);
        return detail ?? throw new InvalidOperationException("Meta campaign sourcing row could not be reloaded.");
    }

    public async Task EnsureMetaAdsCampaignRowAsync(
        Guid orgId,
        Guid campaignId,
        Guid jobId,
        string name,
        string sourcingStatus,
        decimal? dailyBudget,
        string? landingPageUrl,
        string metaCampaignId,
        string adAccountId,
        CancellationToken cancellationToken = default)
    {
        await EnsureCampaignsSchemaAsync(cancellationToken);

        const string sql = @"
IF NOT EXISTS (SELECT 1 FROM dbo.sourcing_campaigns WHERE id = @campaignId)
BEGIN
    INSERT INTO dbo.sourcing_campaigns (
        id, org_id, job_id, name, platform, status,
        daily_budget, currency, landing_page_url,
        external_campaign_id, external_ad_account_id,
        created_at, updated_at
    )
    VALUES (
        @campaignId, @orgId, @jobId, @name, N'meta', @status,
        @dailyBudget, N'USD', @landingPageUrl,
        @metaCampaignId, @adAccountId,
        SYSUTCDATETIME(), SYSUTCDATETIME()
    );
END";

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                campaignId,
                orgId,
                jobId,
                name,
                status = sourcingStatus,
                dailyBudget,
                landingPageUrl,
                metaCampaignId,
                adAccountId
            },
            cancellationToken: cancellationToken));
    }

    public async Task<SourcingCampaignDetailDto> CreateCampaignAsync(Guid orgId, CreateSourcingCampaignRequestDto dto)
    {
        await EnsureCampaignsSchemaAsync();
        var id = Guid.NewGuid();
        var status = string.IsNullOrWhiteSpace(dto.Status) ? SourcingConstants.DefaultCampaignStatus : dto.Status.Trim();
        var currency = string.IsNullOrWhiteSpace(dto.Currency) ? SourcingConstants.DefaultCurrency : dto.Currency.Trim();

        const string sql = @"
            INSERT INTO sourcing_campaigns (
                id, org_id, job_id, name, platform, status,
                daily_budget, total_budget, currency,
                start_date, end_date, landing_page_url, tracking_code,
                external_campaign_id, external_ad_account_id,
                created_at, updated_at
            )
            VALUES (
                @id, @orgId, @jobId, @name, @platform, @status,
                @dailyBudget, @totalBudget, @currency,
                @startDate, @endDate, @landingPageUrl, @trackingCode,
                @externalCampaignId, @externalAdAccountId,
                SYSUTCDATETIME(), SYSUTCDATETIME()
            );";

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(sql, new
        {
            id,
            orgId,
            dto.JobId,
            dto.Name,
            dto.Platform,
            status,
            dto.DailyBudget,
            dto.TotalBudget,
            currency,
            dto.StartDate,
            dto.EndDate,
            dto.LandingPageUrl,
            dto.TrackingCode,
            dto.ExternalCampaignId,
            dto.ExternalAdAccountId
        });

        var detail = await GetCampaignByIdAsync(orgId, id);
        return detail ?? throw new InvalidOperationException("Campaign was created but could not be reloaded.");
    }

    public async Task<Guid?> GetCampaignOrgIdAsync(Guid campaignId)
    {
        const string sql = @"SELECT org_id FROM sourcing_campaigns WHERE id = @campaignId;";
        using var connection = _connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<Guid?>(sql, new { campaignId });
    }

    public async Task<SourcingCampaignDetailDto?> GetCampaignByIdAsync(Guid orgId, Guid campaignId)
    {
        const string sql = @"
            SELECT
                sc.id AS Id,
                sc.job_id AS JobId,
                j.title AS JobTitle,
                sc.name AS Name,
                sc.platform AS Platform,
                sc.status AS Status,
                sc.daily_budget AS DailyBudget,
                sc.total_budget AS TotalBudget,
                sc.currency AS Currency,
                sc.start_date AS StartDate,
                sc.end_date AS EndDate,
                sc.landing_page_url AS LandingPageUrl,
                sc.tracking_code AS TrackingCode,
                sc.external_campaign_id AS ExternalCampaignId,
                sc.external_ad_account_id AS ExternalAdAccountId,
                sc.created_at AS CreatedAt,
                sc.updated_at AS UpdatedAt
            FROM sourcing_campaigns sc
            LEFT JOIN jobs j ON j.id = sc.job_id AND j.org_id = @orgId
            WHERE sc.org_id = @orgId AND sc.id = @campaignId;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<SourcingCampaignDetailDto>(sql, new { orgId, campaignId });
    }

    public async Task<SourcingCampaignDetailDto?> UpdateCampaignAsync(Guid orgId, Guid campaignId, UpdateSourcingCampaignRequestDto dto)
    {
        const string sql = @"
            UPDATE sourcing_campaigns
            SET
                job_id = @jobId,
                name = COALESCE(@name, name),
                platform = COALESCE(@platform, platform),
                daily_budget = @dailyBudget,
                total_budget = @totalBudget,
                currency = COALESCE(@currency, currency),
                start_date = @startDate,
                end_date = @endDate,
                landing_page_url = @landingPageUrl,
                tracking_code = @trackingCode,
                external_campaign_id = @externalCampaignId,
                external_ad_account_id = @externalAdAccountId,
                updated_at = SYSUTCDATETIME()
            WHERE org_id = @orgId AND id = @campaignId;";

        using var connection = _connectionFactory.CreateConnection();
        var n = await connection.ExecuteAsync(sql, new
        {
            orgId,
            campaignId,
            dto.JobId,
            dto.Name,
            dto.Platform,
            dto.DailyBudget,
            dto.TotalBudget,
            dto.Currency,
            dto.StartDate,
            dto.EndDate,
            dto.LandingPageUrl,
            dto.TrackingCode,
            dto.ExternalCampaignId,
            dto.ExternalAdAccountId
        });

        if (n == 0)
            return null;

        return await GetCampaignByIdAsync(orgId, campaignId);
    }

    public async Task<SourcingCampaignDetailDto?> PatchCampaignStatusAsync(Guid orgId, Guid campaignId, string status)
    {
        const string sql = @"
            UPDATE sourcing_campaigns
            SET status = @status,
                updated_at = SYSUTCDATETIME()
            WHERE org_id = @orgId AND id = @campaignId;";

        using var connection = _connectionFactory.CreateConnection();
        var n = await connection.ExecuteAsync(sql, new { orgId, campaignId, status });
        if (n == 0)
            return null;

        return await GetCampaignByIdAsync(orgId, campaignId);
    }

    public async Task<bool> DeleteCampaignAsync(Guid orgId, Guid campaignId)
    {
        const string updateLeadsSql = @"
            UPDATE dbo.sourcing_leads
            SET campaign_id = NULL, updated_at = SYSUTCDATETIME()
            WHERE org_id = @orgId AND campaign_id = @campaignId;";

        const string updateTrackingSql = @"
            UPDATE dbo.sourcing_tracking_events
            SET campaign_id = NULL
            WHERE org_id = @orgId AND campaign_id = @campaignId;";

        const string deleteMetricsSql = @"
            IF OBJECT_ID(N'dbo.sourcing_campaign_metrics', N'U') IS NOT NULL
                DELETE FROM dbo.sourcing_campaign_metrics WHERE campaign_id = @campaignId;";

        const string deleteCampaignSql = @"
            DELETE FROM dbo.sourcing_campaigns WHERE org_id = @orgId AND id = @campaignId;";

        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var tx = connection.BeginTransaction();
        try
        {
            await connection.ExecuteAsync(updateLeadsSql, new { orgId, campaignId }, tx);
            await connection.ExecuteAsync(updateTrackingSql, new { orgId, campaignId }, tx);
            await connection.ExecuteAsync(deleteMetricsSql, new { campaignId }, tx);
            var removed = await connection.ExecuteAsync(deleteCampaignSql, new { orgId, campaignId }, tx);
            tx.Commit();
            return removed == 1;
        }
        catch (SqlException ex) when (ex.Number == 547)
        {
            tx.Rollback();
            _logger.LogWarning(
                ex,
                "FK violation deleting sourcing campaign {CampaignId} for org {OrgId}",
                campaignId,
                orgId);
            throw new InvalidOperationException(
                "Cannot delete the campaign while related rows still reference it (e.g. non-nullable links).");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<SourcingTrackingEventCreatedDto> InsertTrackingEventAsync(Guid orgId, CreateSourcingTrackingEventRequestDto dto)
    {
        var id = Guid.NewGuid();

        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        var cols = await GetTrackingEventColumnSetAsync(connection);

        var insertCols = new List<string> { "id", "org_id", "event_type", "created_at" };
        var insertVals = new List<string> { "@id", "@orgId", "@eventType", "SYSUTCDATETIME()" };
        var parameters = new DynamicParameters();
        parameters.Add("id", id);
        parameters.Add("orgId", orgId);
        parameters.Add("eventType", dto.EventType);

        void AddIf(string col, string paramName, object? value)
        {
            if (!cols.Contains(col)) return;
            insertCols.Add(col);
            insertVals.Add("@" + paramName);
            parameters.Add(paramName, value);
        }

        AddIf("campaign_id", "campaignId", dto.CampaignId);
        AddIf("lead_id", "leadId", dto.LeadId);
        AddIf("job_id", "jobId", dto.JobId);
        AddIf("session_id", "sessionId", dto.SessionId);
        AddIf("visitor_id", "visitorId", dto.VisitorId);
        AddIf("ip_address", "ipAddress", dto.IpAddress);
        AddIf("user_agent", "userAgent", dto.UserAgent);
        AddIf("referrer", "referrer", dto.Referrer);
        AddIf("metadata_json", "metadataJson", dto.MetadataJson);

        if (cols.Contains("source_type_code") && !string.IsNullOrWhiteSpace(dto.SourceTypeCode))
        {
            insertCols.Add("source_type_code");
            insertVals.Add("@sourceTypeCode");
            parameters.Add("sourceTypeCode", dto.SourceTypeCode);
        }
        else if (cols.Contains("source_id") && !string.IsNullOrWhiteSpace(dto.SourceTypeCode))
        {
            var srcId = await connection.ExecuteScalarAsync<Guid?>(
                @"SELECT id FROM sourcing_source_types WHERE code = @code;",
                new { code = dto.SourceTypeCode });
            if (srcId.HasValue)
            {
                insertCols.Add("source_id");
                insertVals.Add("@sourceId");
                parameters.Add("sourceId", srcId.Value);
            }
        }

        var sql = $@"
            INSERT INTO sourcing_tracking_events ({string.Join(", ", insertCols)})
            VALUES ({string.Join(", ", insertVals)});";

        await connection.ExecuteAsync(sql, parameters);

        return new SourcingTrackingEventCreatedDto { Id = id };
    }

    private async Task<HashSet<string>> GetTrackingEventColumnSetAsync(IDbConnection connection)
    {
        var key = connection.ConnectionString ?? "default";
        if (TrackingEventColumnsCache.TryGetValue(key, out var cached))
            return cached;

        const string sql = @"
            SELECT LOWER(COLUMN_NAME) AS c
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'sourcing_tracking_events';";

        var set = (await connection.QueryAsync<string>(sql)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        TrackingEventColumnsCache[key] = set;
        return set;
    }

    public async Task UpdateLeadAfterConvertAsync(
        Guid orgId,
        Guid leadId,
        Guid candidateId,
        Guid? applicationId,
        IDbTransaction transaction)
    {
        const string sql = @"
            UPDATE sourcing_leads
            SET
                status = N'converted',
                converted_candidate_id = @candidateId,
                converted_application_id = @applicationId,
                converted_at = SYSUTCDATETIME(),
                updated_at = SYSUTCDATETIME()
            WHERE org_id = @orgId AND id = @leadId;";

        var connection = transaction.Connection ?? throw new InvalidOperationException("Transaction requires connection.");
        await connection.ExecuteAsync(sql, new { orgId, leadId, candidateId, applicationId }, transaction);
    }

    public async Task InsertLeadEventAsync(
        Guid orgId,
        Guid leadId,
        string eventType,
        string? notes,
        IDbTransaction? transaction)
    {
        var connection = transaction?.Connection ?? _connectionFactory.CreateConnection();
        var ownsConnection = transaction == null;
        try
        {
            if (ownsConnection)
                connection.Open();
            else if (connection.State != ConnectionState.Open)
                connection.Open();
            var cols = await GetLeadEventColumnSetAsync(connection);
            var supportsNotes = cols.Contains("notes");

            var sql = supportsNotes
                ? @"INSERT INTO sourcing_lead_events (id, org_id, lead_id, event_type, notes, created_at)
                    VALUES (@id, @orgId, @leadId, @eventType, @notes, SYSUTCDATETIME());"
                : @"INSERT INTO sourcing_lead_events (id, org_id, lead_id, event_type, created_at)
                    VALUES (@id, @orgId, @leadId, @eventType, SYSUTCDATETIME());";

            await connection.ExecuteAsync(sql, new
            {
                id = Guid.NewGuid(),
                orgId,
                leadId,
                eventType,
                notes
            }, transaction);
        }
        finally
        {
            if (ownsConnection)
                connection.Dispose();
        }
    }

    private async Task<HashSet<string>> GetLeadEventColumnSetAsync(IDbConnection connection)
    {
        var key = connection.ConnectionString ?? "default";
        if (LeadEventColumnsCache.TryGetValue(key, out var cached))
            return cached;

        const string sql = @"
            SELECT LOWER(COLUMN_NAME) AS c
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'sourcing_lead_events';";

        var set = (await connection.QueryAsync<string>(sql)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        LeadEventColumnsCache[key] = set;
        return set;
    }

    private async Task<HashSet<string>> GetSourceTypeColumnSetAsync(IDbConnection connection)
    {
        var key = connection.ConnectionString ?? "default";
        if (SourceTypeColumnsCache.TryGetValue(key, out var cached))
            return cached;

        const string sql = @"
            SELECT LOWER(COLUMN_NAME) AS c
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'sourcing_source_types';";

        var set = (await connection.QueryAsync<string>(sql)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        SourceTypeColumnsCache[key] = set;
        return set;
    }
}
