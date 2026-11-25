using System.Data;
using Dapper;
using nexthire_api.Data;
using nexthire_api.DTOs;
using nexthire_api.DTOs.CoreHr;
using nexthire_api.Services;

namespace nexthire_api.Repositories;

/// <summary>
/// Dapper/SQL Server implementation of the Core HR Documents hub. Reuses dbo.nh_hr_employee_documents
/// (joined to dbo.nh_hr_employees) as the org library — there is no separate catalog table.
/// </summary>
public class HrDocumentRepository : IHrDocumentRepository
{
    private const string SchemaScriptFileName = "012-core-hr-documents-hub.sql";
    private const int DemoMinimumDocuments = 3;
    private const int DemoEmployeeCount = 8;

    /// <summary>Computes the display status in SQL; mirrors <see cref="HrDocumentValidation.ComputeDisplayStatus"/>.</summary>
    private const string DisplayStatusSql = @"(CASE
        WHEN d.status = 'Pending' THEN 'Pending'
        WHEN d.expires_at IS NOT NULL AND d.expires_at < @Today THEN 'Expired'
        WHEN d.status = 'Expired' THEN 'Expired'
        WHEN d.expires_at IS NOT NULL AND d.expires_at <= DATEADD(day, @WindowDays, @Today) THEN 'Expiring'
        ELSE 'Active' END)";

    private const string EmployeeNameSql =
        "COALESCE(NULLIF(LTRIM(RTRIM(e.preferred_name)), ''), LTRIM(RTRIM(e.first_name + ' ' + e.last_name)))";

    private const string FromJoins = @"
FROM dbo.nh_hr_employee_documents d
JOIN dbo.nh_hr_employees e ON e.id = d.employee_id AND e.organization_id = d.organization_id
LEFT JOIN dbo.nh_hr_departments dep ON dep.id = e.department_id AND dep.organization_id = e.organization_id
LEFT JOIN dbo.nh_hr_job_titles jt ON jt.id = e.job_title_id AND jt.organization_id = e.organization_id";

    private static readonly string SelectColumns = $@"
SELECT d.id AS Id, d.employee_id AS EmployeeId,
       {EmployeeNameSql} AS EmployeeName,
       e.employee_number AS EmployeeNumber,
       jt.name AS JobTitle, dep.name AS Department, e.profile_picture_url AS ProfilePictureUrl,
       d.category AS Category, d.name AS Name, d.status AS Status,
       {DisplayStatusSql} AS DisplayStatus,
       d.expires_at AS ExpiresAt, d.document_id AS DocumentId, d.external_url AS ExternalUrl,
       d.file_size_bytes AS FileSizeBytes, d.content_type AS ContentType, d.created_at AS CreatedAt,
       (SELECT TOP 1 a.created_by_name FROM dbo.nh_hr_employee_activity a
         WHERE a.organization_id = d.organization_id AND a.employee_id = d.employee_id
           AND a.created_by = d.created_by AND a.created_by_name IS NOT NULL
         ORDER BY a.created_at DESC) AS CreatedByName";

    private static readonly Dictionary<string, string> SortColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = "d.name {dir}",
        ["category"] = "d.category {dir}",
        ["employee"] = "e.last_name {dir}, e.first_name {dir}",
        ["status"] = DisplayStatusSql + " {dir}",
        ["expiresAt"] = "CASE WHEN d.expires_at IS NULL THEN 1 ELSE 0 END, d.expires_at {dir}",
        ["createdAt"] = "d.created_at {dir}",
        ["size"] = "CASE WHEN d.file_size_bytes IS NULL THEN 1 ELSE 0 END, d.file_size_bytes {dir}"
    };

    private static readonly (string Category, string Name, int? ExpiresInDays, string Status)[] DemoTemplates =
    [
        (HrDocumentCategories.Contract, "Employment Agreement", null, "Active"),
        (HrDocumentCategories.Identification, "National ID", 18, "Active"),
        (HrDocumentCategories.Certification, "Safety Training Certificate", -12, "Active"),
        (HrDocumentCategories.Medical, "Medical Clearance", 210, "Active"),
        (HrDocumentCategories.Tax, "Tax Registration Certificate", null, "Active"),
        (HrDocumentCategories.Policy, "Code of Conduct Acknowledgement", null, "Pending"),
        (HrDocumentCategories.Legal, "Non-Disclosure Agreement", null, "Active"),
        (HrDocumentCategories.Administrative, "Bank Details Form", null, "Active"),
        (HrDocumentCategories.Identification, "Work Permit", 75, "Active")
    ];

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<HrDocumentRepository> _logger;

    public HrDocumentRepository(IDbConnectionFactory connectionFactory, ILogger<HrDocumentRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // Schema / demo data
    // -------------------------------------------------------------------------

    public async Task EnsureSchemaAsync()
    {
        var path = ResolveSchemaScriptPath();
        var sql = await File.ReadAllTextAsync(path);
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        await connection.ExecuteAsync(new CommandDefinition(sql));
    }

    public async Task SeedDemoDocumentsIfEmptyAsync(Guid organizationId, Guid? actorUserId, bool isDevelopment, CancellationToken cancellationToken = default)
    {
        if (!isDevelopment) return;

        using var connection = OpenConnection();

        var documentCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM dbo.nh_hr_employee_documents WHERE organization_id = @OrganizationId;",
            new { OrganizationId = organizationId }, cancellationToken: cancellationToken));
        if (documentCount >= DemoMinimumDocuments) return;

        var employeeIds = (await connection.QueryAsync<Guid>(new CommandDefinition(
            @"SELECT TOP (@Take) id FROM dbo.nh_hr_employees
              WHERE organization_id = @OrganizationId AND archived_at IS NULL
              ORDER BY hire_date, employee_number;",
            new { OrganizationId = organizationId, Take = DemoEmployeeCount }, cancellationToken: cancellationToken))).ToList();
        if (employeeIds.Count == 0) return;

        var today = DateTime.UtcNow.Date;
        var now = DateTimeOffset.UtcNow;

        using var tx = connection.BeginTransaction();
        try
        {
            for (var i = 0; i < employeeIds.Count; i++)
            {
                for (var k = 0; k < 3; k++)
                {
                    var template = DemoTemplates[(i * 3 + k) % DemoTemplates.Length];
                    var slug = template.Name.ToLowerInvariant().Replace(' ', '-');
                    await connection.ExecuteAsync(new CommandDefinition(InsertSql, new
                    {
                        Id = Guid.NewGuid(),
                        OrganizationId = organizationId,
                        EmployeeId = employeeIds[i],
                        template.Category,
                        template.Name,
                        DocumentId = (Guid?)null,
                        ExternalUrl = $"https://example.com/demo-documents/{slug}.pdf",
                        template.Status,
                        ExpiresAt = template.ExpiresInDays.HasValue ? today.AddDays(template.ExpiresInDays.Value) : (DateTime?)null,
                        ContentType = "application/pdf",
                        FileSizeBytes = (long?)(120_000 + (i * 3 + k) * 37_500),
                        Now = now.AddMinutes(-(i * 3 + k)),
                        ActorUserId = actorUserId
                    }, tx, cancellationToken: cancellationToken));
                }
            }

            var requestCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(1) FROM dbo.nh_hr_document_requests WHERE organization_id = @OrganizationId;",
                new { OrganizationId = organizationId }, tx, cancellationToken: cancellationToken));
            if (requestCount == 0)
            {
                await connection.ExecuteAsync(new CommandDefinition(InsertRequestSql, new
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = organizationId,
                    EmployeeId = employeeIds[0],
                    Category = HrDocumentCategories.Identification,
                    Title = "Updated proof of address",
                    DueAt = (DateTime?)today.AddDays(14),
                    Notes = "Demo request",
                    Now = now,
                    ActorUserId = actorUserId
                }, tx, cancellationToken: cancellationToken));
            }

            tx.Commit();
            _logger.LogInformation("Seeded demo HR documents for organization {OrganizationId}.", organizationId);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // -------------------------------------------------------------------------
    // Read
    // -------------------------------------------------------------------------

    public async Task<PagedResult<HrDocumentListItemDto>> ListAsync(Guid organizationId, HrDocumentListQuery query, DateTime today, CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var offset = (page - 1) * pageSize;

        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var searchLike = search == null ? null : "%" + EscapeLike(search) + "%";
        var status = HrDocumentValidation.NormalizeDisplayStatus(query.Status);
        var category = HrDocumentValidation.NormalizeCategory(query.Category);

        // Dapper sends untyped nulls as nvarchar; DATEADD(day, @ExpiringWithinDays, ...) then fails
        // with "nvarchar is invalid for argument 2". Use a sentinel + explicit DbType.Int32.
        var expiringWithinDays = query.ExpiringWithinDays.HasValue ? Math.Max(0, query.ExpiringWithinDays.Value) : -1;

        var parameters = new DynamicParameters();
        parameters.Add("OrganizationId", organizationId);
        parameters.Add("Today", today.Date, DbType.Date);
        parameters.Add("WindowDays", HrDocumentDisplayStatuses.ExpiringWindowDays, DbType.Int32);
        parameters.Add("EmployeeId", query.EmployeeId);
        parameters.Add("DepartmentId", query.DepartmentId);
        parameters.Add("Status", status);
        parameters.Add("ExpiringWithinDays", expiringWithinDays, DbType.Int32);
        parameters.Add("SearchLike", searchLike);
        parameters.Add("Offset", offset, DbType.Int32);
        parameters.Add("PageSize", pageSize, DbType.Int32);

        var categoryClause = string.Empty;
        if (category == HrDocumentCategories.Other)
        {
            parameters.Add("KnownCategories", HrDocumentValidation.KnownNonOtherStoredValues().ToArray());
            categoryClause = "AND d.category NOT IN @KnownCategories";
        }
        else if (category != null)
        {
            parameters.Add("Categories", HrDocumentValidation.StoredValuesFor(category).ToArray());
            categoryClause = "AND d.category IN @Categories";
        }
        else if (!string.IsNullOrWhiteSpace(query.Category))
        {
            // Unknown category filter: nothing can match.
            categoryClause = "AND 1 = 0";
        }

        var where = $@"
WHERE d.organization_id = @OrganizationId
  AND d.archived_at IS NULL
  AND e.archived_at IS NULL
  AND (@EmployeeId IS NULL OR d.employee_id = @EmployeeId)
  AND (@DepartmentId IS NULL OR e.department_id = @DepartmentId)
  {categoryClause}
  AND (@Status IS NULL OR {DisplayStatusSql} = @Status)
  AND (@ExpiringWithinDays < 0 OR (d.expires_at IS NOT NULL AND d.expires_at >= @Today
        AND d.expires_at <= DATEADD(day, @ExpiringWithinDays, @Today)))
  AND (
        @SearchLike IS NULL
        OR d.name LIKE @SearchLike ESCAPE '\'
        OR d.category LIKE @SearchLike ESCAPE '\'
        OR e.first_name LIKE @SearchLike ESCAPE '\'
        OR e.last_name LIKE @SearchLike ESCAPE '\'
        OR ISNULL(e.preferred_name, '') LIKE @SearchLike ESCAPE '\'
        OR (e.first_name + ' ' + e.last_name) LIKE @SearchLike ESCAPE '\'
        OR e.employee_number LIKE @SearchLike ESCAPE '\'
        OR ISNULL(dep.name, '') LIKE @SearchLike ESCAPE '\'
        OR ISNULL(jt.name, '') LIKE @SearchLike ESCAPE '\'
      )";

        var countSql = "SELECT COUNT(1) " + FromJoins + where + ";";
        var pageSql = SelectColumns + FromJoins + where + $@"
ORDER BY {BuildOrderBy(query.Sort, query.Dir)}, d.id
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";

        using var connection = OpenConnection();
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(countSql, parameters, cancellationToken: cancellationToken));
        var rows = (await connection.QueryAsync<HrDocumentListItemDto>(new CommandDefinition(pageSql, parameters, cancellationToken: cancellationToken))).ToList();
        foreach (var row in rows) row.Category = HrDocumentValidation.NormalizeCategoryForDisplay(row.Category);

        return new PagedResult<HrDocumentListItemDto>
        {
            Items = rows,
            Page = page,
            PageSize = pageSize,
            Total = total
        };
    }

    public async Task<HrDocumentDetailDto?> GetByIdAsync(Guid organizationId, Guid id, DateTime today, CancellationToken cancellationToken = default)
    {
        var sql = SelectColumns + @",
       d.updated_at AS UpdatedAt, d.archived_at AS ArchivedAt, CAST(0 AS bit) AS IsArchived"
            + FromJoins + @"
WHERE d.organization_id = @OrganizationId AND d.id = @Id AND d.archived_at IS NULL AND e.archived_at IS NULL;";

        using var connection = OpenConnection();
        var row = await connection.QuerySingleOrDefaultAsync<HrDocumentDetailDto>(new CommandDefinition(sql,
            new { OrganizationId = organizationId, Id = id, Today = today.Date, WindowDays = HrDocumentDisplayStatuses.ExpiringWindowDays },
            cancellationToken: cancellationToken));
        if (row != null) row.Category = HrDocumentValidation.NormalizeCategoryForDisplay(row.Category);
        return row;
    }

    public async Task<IReadOnlyList<HrDocumentActivityDto>> GetActivityAsync(Guid organizationId, Guid employeeId, Guid documentRowId, int take, CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT TOP (@Take) id AS Id, activity_type AS ActivityType, summary AS Summary,
       created_at AS CreatedAt, created_by_name AS CreatedByName
FROM dbo.nh_hr_employee_activity
WHERE organization_id = @OrganizationId AND employee_id = @EmployeeId
  AND activity_type IN ('DocumentAdded', 'DocumentUpdated', 'DocumentArchived')
  AND JSON_VALUE(details_json, '$.documentId') = @DocumentRowId
ORDER BY created_at DESC;";

        using var connection = OpenConnection();
        var rows = await connection.QueryAsync<HrDocumentActivityDto>(new CommandDefinition(sql,
            new
            {
                OrganizationId = organizationId,
                EmployeeId = employeeId,
                DocumentRowId = documentRowId.ToString("D"),
                Take = Math.Clamp(take, 1, 200)
            }, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    public async Task<HrDocumentSummaryDto> SummaryAsync(Guid organizationId, DateTime today, CancellationToken cancellationToken = default)
    {
        var sql = $@"
SELECT COUNT(1) AS Total,
       ISNULL(SUM(CASE WHEN x.st = 'Active' THEN 1 ELSE 0 END), 0) AS ValidCount,
       ISNULL(SUM(CASE WHEN x.st = 'Expiring' THEN 1 ELSE 0 END), 0) AS ExpiringCount,
       ISNULL(SUM(CASE WHEN x.st = 'Expired' THEN 1 ELSE 0 END), 0) AS ExpiredCount,
       ISNULL(SUM(CASE WHEN x.st = 'Pending' THEN 1 ELSE 0 END), 0) AS PendingCount
FROM (
    SELECT {DisplayStatusSql} AS st
    FROM dbo.nh_hr_employee_documents d
    JOIN dbo.nh_hr_employees e ON e.id = d.employee_id AND e.organization_id = d.organization_id
    WHERE d.organization_id = @OrganizationId AND d.archived_at IS NULL AND e.archived_at IS NULL
) x;";

        using var connection = OpenConnection();
        var counts = await connection.QuerySingleAsync<SummaryRow>(new CommandDefinition(sql,
            new { OrganizationId = organizationId, Today = today.Date, WindowDays = HrDocumentDisplayStatuses.ExpiringWindowDays },
            cancellationToken: cancellationToken));

        var byCategory = await CategoryCountsCoreAsync(connection, organizationId, cancellationToken);
        var openRequests = await CountOpenRequestsCoreAsync(connection, organizationId, cancellationToken);

        return new HrDocumentSummaryDto
        {
            TotalDocuments = counts.Total,
            CompletedOrValid = counts.ValidCount,
            ExpiringIn30Days = counts.ExpiringCount,
            Expired = counts.ExpiredCount,
            Pending = counts.PendingCount,
            ByCategory = byCategory,
            OpenRequests = openRequests
        };
    }

    public async Task<IReadOnlyList<HrDocumentCategoryCountDto>> CategoryCountsAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        return await CategoryCountsCoreAsync(connection, organizationId, cancellationToken);
    }

    private static async Task<IReadOnlyList<HrDocumentCategoryCountDto>> CategoryCountsCoreAsync(IDbConnection connection, Guid organizationId, CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT d.category AS Category, COUNT(1) AS Count
FROM dbo.nh_hr_employee_documents d
JOIN dbo.nh_hr_employees e ON e.id = d.employee_id AND e.organization_id = d.organization_id
WHERE d.organization_id = @OrganizationId AND d.archived_at IS NULL AND e.archived_at IS NULL
GROUP BY d.category;";

        var rows = await connection.QueryAsync<HrDocumentCategoryCountDto>(new CommandDefinition(sql,
            new { OrganizationId = organizationId }, cancellationToken: cancellationToken));

        var merged = HrDocumentCategories.All.ToDictionary(c => c, _ => 0);
        foreach (var row in rows)
            merged[HrDocumentValidation.NormalizeCategoryForDisplay(row.Category)] += row.Count;

        return HrDocumentCategories.All
            .Select(c => new HrDocumentCategoryCountDto { Category = c, Count = merged[c] })
            .ToList();
    }

    // -------------------------------------------------------------------------
    // Write
    // -------------------------------------------------------------------------

    private const string InsertSql = @"
INSERT INTO dbo.nh_hr_employee_documents
    (id, organization_id, employee_id, category, name, document_id, external_url, status, expires_at,
     file_size_bytes, content_type, created_at, created_by)
VALUES
    (@Id, @OrganizationId, @EmployeeId, @Category, @Name, @DocumentId, @ExternalUrl, @Status, @ExpiresAt,
     @FileSizeBytes, @ContentType, @Now, @ActorUserId);";

    public async Task<Guid> InsertAsync(Guid organizationId, HrDocumentInsert row, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(InsertSql, new
        {
            Id = id,
            OrganizationId = organizationId,
            row.EmployeeId,
            Category = row.Category,
            Name = row.Name,
            row.DocumentId,
            row.ExternalUrl,
            row.Status,
            ExpiresAt = row.ExpiresAt?.Date,
            row.FileSizeBytes,
            row.ContentType,
            Now = DateTimeOffset.UtcNow,
            ActorUserId = actorUserId
        }, cancellationToken: cancellationToken));
        return id;
    }

    public async Task<bool> UpdateAsync(
        Guid organizationId,
        Guid id,
        string category,
        string name,
        string? status,
        DateTime? expiresAt,
        string? externalUrl,
        Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
UPDATE dbo.nh_hr_employee_documents
SET category = @Category,
    name = @Name,
    status = COALESCE(@Status, status),
    expires_at = @ExpiresAt,
    external_url = COALESCE(@ExternalUrl, external_url),
    updated_at = @Now,
    updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND id = @Id AND archived_at IS NULL;";

        using var connection = OpenConnection();
        var rows = await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            OrganizationId = organizationId,
            Id = id,
            Category = category,
            Name = name,
            Status = status,
            ExpiresAt = expiresAt?.Date,
            ExternalUrl = externalUrl,
            Now = DateTimeOffset.UtcNow,
            ActorUserId = actorUserId
        }, cancellationToken: cancellationToken));
        return rows > 0;
    }

    public async Task<bool> ArchiveAsync(Guid organizationId, Guid id, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
UPDATE dbo.nh_hr_employee_documents
SET archived_at = @Now, archived_by = @ActorUserId, status = 'Archived', updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND id = @Id AND archived_at IS NULL;";

        using var connection = OpenConnection();
        var rows = await connection.ExecuteAsync(new CommandDefinition(sql,
            new { OrganizationId = organizationId, Id = id, Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId },
            cancellationToken: cancellationToken));
        return rows > 0;
    }

    // -------------------------------------------------------------------------
    // Document requests
    // -------------------------------------------------------------------------

    private const string RequestSelect = @"
SELECT r.id AS Id, r.employee_id AS EmployeeId,
       " + EmployeeNameSql + @" AS EmployeeName, e.profile_picture_url AS ProfilePictureUrl,
       r.category AS Category, r.title AS Title, r.status AS Status, r.due_at AS DueAt, r.notes AS Notes,
       r.fulfilled_document_id AS FulfilledDocumentId, r.created_at AS CreatedAt, r.completed_at AS CompletedAt
FROM dbo.nh_hr_document_requests r
JOIN dbo.nh_hr_employees e ON e.id = r.employee_id AND e.organization_id = r.organization_id";

    private const string InsertRequestSql = @"
INSERT INTO dbo.nh_hr_document_requests
    (id, organization_id, employee_id, category, title, status, due_at, notes, created_at, created_by)
VALUES
    (@Id, @OrganizationId, @EmployeeId, @Category, @Title, 'Open', @DueAt, @Notes, @Now, @ActorUserId);";

    public async Task<IReadOnlyList<DocumentRequestDto>> ListRequestsAsync(
        Guid organizationId,
        IReadOnlyCollection<string>? statuses,
        Guid? employeeId,
        DateTime today,
        CancellationToken cancellationToken = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("OrganizationId", organizationId);
        parameters.Add("EmployeeId", employeeId);

        var statusClause = string.Empty;
        if (statuses is { Count: > 0 })
        {
            parameters.Add("Statuses", statuses.ToArray());
            statusClause = "AND r.status IN @Statuses";
        }

        var sql = RequestSelect + $@"
WHERE r.organization_id = @OrganizationId AND e.archived_at IS NULL
  AND (@EmployeeId IS NULL OR r.employee_id = @EmployeeId)
  {statusClause}
ORDER BY CASE WHEN r.due_at IS NULL THEN 1 ELSE 0 END, r.due_at, r.created_at DESC;";

        using var connection = OpenConnection();
        var rows = (await connection.QueryAsync<DocumentRequestDto>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))).ToList();
        foreach (var row in rows) NormalizeRequest(row, today);
        return rows;
    }

    public async Task<DocumentRequestDto?> GetRequestAsync(Guid organizationId, Guid id, DateTime today, CancellationToken cancellationToken = default)
    {
        var sql = RequestSelect + " WHERE r.organization_id = @OrganizationId AND r.id = @Id;";
        using var connection = OpenConnection();
        var row = await connection.QuerySingleOrDefaultAsync<DocumentRequestDto>(new CommandDefinition(sql,
            new { OrganizationId = organizationId, Id = id }, cancellationToken: cancellationToken));
        if (row != null) NormalizeRequest(row, today);
        return row;
    }

    public async Task<Guid> CreateRequestAsync(
        Guid organizationId,
        Guid employeeId,
        string category,
        string title,
        DateTime? dueAt,
        string? notes,
        Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(InsertRequestSql, new
        {
            Id = id,
            OrganizationId = organizationId,
            EmployeeId = employeeId,
            Category = category,
            Title = title,
            DueAt = dueAt?.Date,
            Notes = notes,
            Now = DateTimeOffset.UtcNow,
            ActorUserId = actorUserId
        }, cancellationToken: cancellationToken));
        return id;
    }

    public async Task<bool> FulfillRequestAsync(Guid organizationId, Guid id, Guid? fulfilledDocumentRowId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
UPDATE dbo.nh_hr_document_requests
SET status = 'Fulfilled', completed_at = @Now, fulfilled_document_id = @FulfilledDocumentId
WHERE organization_id = @OrganizationId AND id = @Id AND status IN ('Open', 'Overdue');";

        using var connection = OpenConnection();
        var rows = await connection.ExecuteAsync(new CommandDefinition(sql,
            new { OrganizationId = organizationId, Id = id, FulfilledDocumentId = fulfilledDocumentRowId, Now = DateTimeOffset.UtcNow },
            cancellationToken: cancellationToken));
        return rows > 0;
    }

    public async Task<bool> CancelRequestAsync(Guid organizationId, Guid id, CancellationToken cancellationToken = default)
    {
        const string sql = @"
UPDATE dbo.nh_hr_document_requests
SET status = 'Cancelled', completed_at = @Now
WHERE organization_id = @OrganizationId AND id = @Id AND status IN ('Open', 'Overdue');";

        using var connection = OpenConnection();
        var rows = await connection.ExecuteAsync(new CommandDefinition(sql,
            new { OrganizationId = organizationId, Id = id, Now = DateTimeOffset.UtcNow },
            cancellationToken: cancellationToken));
        return rows > 0;
    }

    public async Task<int> CountOpenRequestsAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        return await CountOpenRequestsCoreAsync(connection, organizationId, cancellationToken);
    }

    private static Task<int> CountOpenRequestsCoreAsync(IDbConnection connection, Guid organizationId, CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT COUNT(1)
FROM dbo.nh_hr_document_requests r
JOIN dbo.nh_hr_employees e ON e.id = r.employee_id AND e.organization_id = r.organization_id
WHERE r.organization_id = @OrganizationId AND e.archived_at IS NULL AND r.status IN ('Open', 'Overdue');";

        return connection.ExecuteScalarAsync<int>(new CommandDefinition(sql,
            new { OrganizationId = organizationId }, cancellationToken: cancellationToken));
    }

    private static void NormalizeRequest(DocumentRequestDto row, DateTime today)
    {
        row.Category = HrDocumentValidation.NormalizeCategoryForDisplay(row.Category);
        row.Status = HrDocumentValidation.ComputeRequestStatus(row.Status, row.DueAt, today);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private IDbConnection OpenConnection()
    {
        var connection = _connectionFactory.CreateConnection();
        connection.Open();
        return connection;
    }

    private static string BuildOrderBy(string? sort, string? dir)
    {
        var direction = string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
        var template = !string.IsNullOrWhiteSpace(sort) && SortColumns.TryGetValue(sort.Trim(), out var t)
            ? t
            : SortColumns["createdAt"];
        return template.Replace("{dir}", direction);
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    private static string ResolveSchemaScriptPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "sql", SchemaScriptFileName),
            Path.Combine(Directory.GetCurrentDirectory(), "sql", SchemaScriptFileName)
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            $"Core HR documents hub schema script not found. Expected sql/{SchemaScriptFileName}.");
    }

    private sealed class SummaryRow
    {
        public int Total { get; set; }
        public int ValidCount { get; set; }
        public int ExpiringCount { get; set; }
        public int ExpiredCount { get; set; }
        public int PendingCount { get; set; }
    }
}
