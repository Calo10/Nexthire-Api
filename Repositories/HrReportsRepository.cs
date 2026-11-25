using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using Dapper;
using nexthire_api.Data;
using nexthire_api.DTOs.CoreHr;
using nexthire_api.Services.Reporting;

namespace nexthire_api.Repositories;

/// <summary>
/// Dapper/SQL Server implementation of Core HR Reports &amp; People Analytics. Every query is tenant scoped by
/// organization_id and archives are soft. Analytics live in <c>HrReportsRepository.Analytics.cs</c>.
/// </summary>
public partial class HrReportsRepository : IHrReportsRepository
{
    private const string SchemaScriptFileName = "014-core-hr-reports-schema.sql";
    private const int QueryTimeoutSeconds = 30;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<HrReportsRepository> _logger;

    public HrReportsRepository(IDbConnectionFactory connectionFactory, ILogger<HrReportsRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // Schema / system reports
    // -------------------------------------------------------------------------

    public async Task EnsureSchemaAsync()
    {
        var path = ResolveSchemaScriptPath();
        var sql = await File.ReadAllTextAsync(path);
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        await connection.ExecuteAsync(new CommandDefinition(sql));
    }

    public async Task EnsureSystemReportsAsync(Guid organizationId, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
IF EXISTS (SELECT 1 FROM dbo.nh_hr_report_definitions WHERE organization_id = @OrganizationId AND id = @Id)
BEGIN
    UPDATE dbo.nh_hr_report_definitions
    SET name = @Name, description = @Description, category = @Category, dataset_key = @DatasetKey,
        configuration_json = @ConfigurationJson, visualization_json = @VisualizationJson,
        updated_at = @Now
    WHERE organization_id = @OrganizationId AND id = @Id AND is_system = 1
      AND (name <> @Name OR ISNULL(description, N'') <> ISNULL(@Description, N'') OR category <> @Category
           OR dataset_key <> @DatasetKey OR configuration_json <> @ConfigurationJson);
END
ELSE
BEGIN
    INSERT INTO dbo.nh_hr_report_definitions
        (id, organization_id, name, description, category, dataset_key, configuration_json, visualization_json,
         owner_user_id, visibility, is_system, configuration_version, created_at, created_by)
    VALUES
        (@Id, @OrganizationId, @Name, @Description, @Category, @DatasetKey, @ConfigurationJson, @VisualizationJson,
         NULL, N'Organization', 1, 1, @Now, @ActorUserId);
END";

        using var connection = OpenConnection();
        var now = DateTimeOffset.UtcNow;
        foreach (var seed in SystemReportCatalog.All)
        {
            try
            {
                await connection.ExecuteAsync(new CommandDefinition(sql, new
                {
                    OrganizationId = organizationId,
                    Id = SystemReportCatalog.IdFor(organizationId, seed.Key),
                    seed.Name,
                    seed.Description,
                    seed.Category,
                    seed.DatasetKey,
                    seed.ConfigurationJson,
                    seed.VisualizationJson,
                    Now = now,
                    ActorUserId = actorUserId
                }, cancellationToken: cancellationToken));
            }
            catch (SqlException ex) when (IsDuplicateKey(ex))
            {
                // Another request seeded the same card concurrently.
            }
        }
    }

    // -------------------------------------------------------------------------
    // Report definitions
    // -------------------------------------------------------------------------

    private const string ReportSelectSql = @"
SELECT r.id AS Id, r.name AS Name, r.description AS Description, r.category AS Category,
       r.dataset_key AS DatasetKey, r.configuration_json AS ConfigurationJson, r.visualization_json AS VisualizationJson,
       r.visibility AS Visibility, r.is_system AS IsSystem,
       CAST(CASE WHEN f.report_definition_id IS NULL THEN 0 ELSE 1 END AS bit) AS IsFavorite,
       v.last_viewed_at AS LastViewedAt, r.created_at AS CreatedAt, r.updated_at AS UpdatedAt, r.owner_user_id AS OwnerUserId
FROM dbo.nh_hr_report_definitions r
LEFT JOIN dbo.nh_hr_report_favorites f
       ON f.organization_id = r.organization_id AND f.report_definition_id = r.id AND f.user_id = @UserId
LEFT JOIN dbo.nh_hr_report_views v
       ON v.organization_id = r.organization_id AND v.report_definition_id = r.id AND v.user_id = @UserId";

    public async Task<IReadOnlyList<ReportDefinitionDto>> ListReportsAsync(
        Guid organizationId, Guid userId, bool seeAll, string? category, string? search, bool favoritesOnly,
        CancellationToken cancellationToken = default)
    {
        var sql = ReportSelectSql + @"
WHERE r.organization_id = @OrganizationId AND r.archived_at IS NULL
  AND (@SeeAll = 1 OR r.is_system = 1 OR r.owner_user_id = @UserId OR r.visibility <> N'Private')
  AND (@Category IS NULL OR r.category = @Category)
  AND (@Search IS NULL OR r.name LIKE @Search ESCAPE '\' OR r.description LIKE @Search ESCAPE '\')
  AND (@FavoritesOnly = 0 OR f.report_definition_id IS NOT NULL)
ORDER BY CASE WHEN f.report_definition_id IS NULL THEN 1 ELSE 0 END, r.is_system DESC, r.category, r.name;";

        using var connection = OpenConnection();
        var rows = await connection.QueryAsync<ReportDefinitionDto>(new CommandDefinition(sql, new
        {
            OrganizationId = organizationId,
            UserId = userId,
            SeeAll = seeAll,
            Category = Trim(category),
            Search = string.IsNullOrWhiteSpace(search) ? null : "%" + ReportDatasetRegistry.EscapeLike(search.Trim()) + "%",
            FavoritesOnly = favoritesOnly
        }, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    public async Task<ReportDefinitionDto?> GetReportAsync(Guid organizationId, Guid userId, Guid id, CancellationToken cancellationToken = default)
    {
        var sql = ReportSelectSql + " WHERE r.organization_id = @OrganizationId AND r.id = @Id AND r.archived_at IS NULL;";
        using var connection = OpenConnection();
        return await connection.QuerySingleOrDefaultAsync<ReportDefinitionDto>(new CommandDefinition(sql,
            new { OrganizationId = organizationId, UserId = userId, Id = id }, cancellationToken: cancellationToken));
    }

    public async Task<Guid> InsertReportAsync(Guid organizationId, Guid userId, ReportDefinitionWrite row, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        using var connection = OpenConnection();
        await InsertReportCoreAsync(connection, organizationId, userId, id, row, cancellationToken);
        return id;
    }

    private static Task InsertReportCoreAsync(
        IDbConnection connection, Guid organizationId, Guid userId, Guid id, ReportDefinitionWrite row, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_report_definitions
    (id, organization_id, name, description, category, dataset_key, configuration_json, visualization_json,
     owner_user_id, visibility, is_system, configuration_version, created_at, created_by)
VALUES
    (@Id, @OrganizationId, @Name, @Description, @Category, @DatasetKey, @ConfigurationJson, @VisualizationJson,
     @UserId, @Visibility, 0, 1, @Now, @UserId);", new
        {
            Id = id,
            OrganizationId = organizationId,
            UserId = userId,
            row.Name,
            row.Description,
            row.Category,
            row.DatasetKey,
            row.ConfigurationJson,
            row.VisualizationJson,
            row.Visibility,
            Now = DateTimeOffset.UtcNow
        }, cancellationToken: ct));

    public async Task<bool> UpdateReportAsync(Guid organizationId, Guid userId, Guid id, ReportDefinitionWrite row, CancellationToken cancellationToken = default)
    {
        const string sql = @"
UPDATE dbo.nh_hr_report_definitions
SET name = @Name, description = @Description, category = @Category, dataset_key = @DatasetKey,
    configuration_json = @ConfigurationJson, visualization_json = @VisualizationJson, visibility = @Visibility,
    configuration_version = configuration_version + 1, updated_at = @Now, updated_by = @UserId
WHERE organization_id = @OrganizationId AND id = @Id AND archived_at IS NULL AND is_system = 0;";

        using var connection = OpenConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            OrganizationId = organizationId,
            Id = id,
            UserId = userId,
            row.Name,
            row.Description,
            row.Category,
            row.DatasetKey,
            row.ConfigurationJson,
            row.VisualizationJson,
            row.Visibility,
            Now = DateTimeOffset.UtcNow
        }, cancellationToken: cancellationToken));
        return affected > 0;
    }

    public async Task<bool> ArchiveReportAsync(Guid organizationId, Guid userId, Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow;
        var p = new { OrganizationId = organizationId, Id = id, UserId = userId, Now = now };

        var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_report_definitions
SET archived_at = @Now, archived_by = @UserId, updated_at = @Now, updated_by = @UserId
WHERE organization_id = @OrganizationId AND id = @Id AND archived_at IS NULL AND is_system = 0;", p, tx, cancellationToken: cancellationToken));

        if (affected > 0)
        {
            // A schedule without its report can never run.
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_report_schedules
SET is_active = 0, archived_at = @Now, updated_at = @Now, updated_by = @UserId
WHERE organization_id = @OrganizationId AND report_definition_id = @Id AND archived_at IS NULL;", p, tx, cancellationToken: cancellationToken));
        }

        tx.Commit();
        return affected > 0;
    }

    public async Task<Guid?> DuplicateReportAsync(Guid organizationId, Guid userId, Guid id, string newName, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var source = await connection.QuerySingleOrDefaultAsync<ReportDefinitionDto>(new CommandDefinition(
            ReportSelectSql + " WHERE r.organization_id = @OrganizationId AND r.id = @Id AND r.archived_at IS NULL;",
            new { OrganizationId = organizationId, UserId = userId, Id = id }, cancellationToken: cancellationToken));
        if (source == null) return null;

        var copyId = Guid.NewGuid();
        await InsertReportCoreAsync(connection, organizationId, userId, copyId, new ReportDefinitionWrite(
            newName, source.Description, source.Category, source.DatasetKey, source.ConfigurationJson,
            source.VisualizationJson, "Private"), cancellationToken);
        return copyId;
    }

    public async Task SetFavoriteAsync(Guid organizationId, Guid userId, Guid reportId, bool isFavorite, CancellationToken cancellationToken = default)
    {
        const string addSql = @"
IF NOT EXISTS (SELECT 1 FROM dbo.nh_hr_report_favorites
               WHERE organization_id = @OrganizationId AND user_id = @UserId AND report_definition_id = @ReportId)
    INSERT INTO dbo.nh_hr_report_favorites (organization_id, user_id, report_definition_id, created_at)
    VALUES (@OrganizationId, @UserId, @ReportId, @Now);";
        const string removeSql = @"
DELETE FROM dbo.nh_hr_report_favorites
WHERE organization_id = @OrganizationId AND user_id = @UserId AND report_definition_id = @ReportId;";

        using var connection = OpenConnection();
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(isFavorite ? addSql : removeSql,
                new { OrganizationId = organizationId, UserId = userId, ReportId = reportId, Now = DateTimeOffset.UtcNow },
                cancellationToken: cancellationToken));
        }
        catch (SqlException ex) when (IsDuplicateKey(ex))
        {
            // Already a favorite (concurrent request).
        }
    }

    public async Task RecordViewAsync(Guid organizationId, Guid userId, Guid reportId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
UPDATE dbo.nh_hr_report_views SET last_viewed_at = @Now
WHERE organization_id = @OrganizationId AND user_id = @UserId AND report_definition_id = @ReportId;
IF @@ROWCOUNT = 0
    INSERT INTO dbo.nh_hr_report_views (organization_id, user_id, report_definition_id, last_viewed_at)
    VALUES (@OrganizationId, @UserId, @ReportId, @Now);";

        using var connection = OpenConnection();
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(sql,
                new { OrganizationId = organizationId, UserId = userId, ReportId = reportId, Now = DateTimeOffset.UtcNow },
                cancellationToken: cancellationToken));
        }
        catch (SqlException ex) when (IsDuplicateKey(ex))
        {
            // Concurrent first view.
        }
    }

    // -------------------------------------------------------------------------
    // Dataset query execution
    // -------------------------------------------------------------------------

    public async Task<ReportQueryResult> ExecuteReportQueryAsync(BuiltReportQuery query, CancellationToken cancellationToken = default)
    {
        var parameters = new DynamicParameters();
        foreach (var (key, value) in query.Parameters)
            parameters.Add(key, value);

        using var connection = OpenConnection();
        var raw = await connection.QueryAsync(new CommandDefinition(query.Sql, parameters,
            commandTimeout: QueryTimeoutSeconds, cancellationToken: cancellationToken));

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var truncated = false;
        foreach (var item in raw)
        {
            if (rows.Count >= query.RowLimit)
            {
                truncated = true;
                break;
            }

            var source = (IDictionary<string, object>)item;
            var row = new Dictionary<string, object?>(query.Columns.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var column in query.Columns)
            {
                source.TryGetValue(column.SqlAlias, out var value);
                row[column.Key] = NormalizeValue(value);
            }
            rows.Add(row);
        }

        return new ReportQueryResult(query.Columns, rows, truncated, query.RowLimit);
    }

    private static object? NormalizeValue(object? value) => value switch
    {
        null or DBNull => null,
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : dt.ToString("o", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("o", CultureInfo.InvariantCulture),
        Guid g => g.ToString(),
        double d => Math.Round(d, 2),
        decimal m => Math.Round(m, 2),
        _ => value
    };

    // -------------------------------------------------------------------------
    // Dashboards
    // -------------------------------------------------------------------------

    private sealed class DashboardRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public string Visibility { get; set; } = "Private";
        public bool IsDefault { get; set; }
        public string? GlobalFiltersJson { get; set; }
        public string? LayoutJson { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public Guid OwnerUserId { get; set; }
    }

    private sealed class WidgetRow
    {
        public Guid Id { get; set; }
        public Guid DashboardId { get; set; }
        public Guid? ReportDefinitionId { get; set; }
        public string WidgetType { get; set; } = "Chart";
        public string? Title { get; set; }
        public string ConfigurationJson { get; set; } = "{}";
        public int GridX { get; set; }
        public int GridY { get; set; }
        public int GridW { get; set; }
        public int GridH { get; set; }
        public int SortOrder { get; set; }
    }

    private const string DashboardSelectSql = @"
SELECT d.id AS Id, d.name AS Name, d.description AS Description, d.visibility AS Visibility, d.is_default AS IsDefault,
       d.global_filters_json AS GlobalFiltersJson, d.layout_json AS LayoutJson, d.created_at AS CreatedAt,
       d.owner_user_id AS OwnerUserId
FROM dbo.nh_hr_report_dashboards d";

    public async Task<IReadOnlyList<ReportDashboardDto>> ListDashboardsAsync(Guid organizationId, Guid userId, bool seeAll, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var rows = (await connection.QueryAsync<DashboardRow>(new CommandDefinition(DashboardSelectSql + @"
WHERE d.organization_id = @OrganizationId AND d.archived_at IS NULL
  AND (@SeeAll = 1 OR d.owner_user_id = @UserId OR d.visibility <> N'Private')
ORDER BY d.is_default DESC, d.name;",
            new { OrganizationId = organizationId, UserId = userId, SeeAll = seeAll }, cancellationToken: cancellationToken))).ToList();

        var widgets = await LoadWidgetsAsync(connection, organizationId, rows.Select(r => r.Id).ToArray(), cancellationToken);
        return rows.Select(r => ToDashboardDto(r, widgets)).ToList();
    }

    public async Task<ReportDashboardRecord?> GetDashboardAsync(Guid organizationId, Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var row = await connection.QuerySingleOrDefaultAsync<DashboardRow>(new CommandDefinition(DashboardSelectSql + @"
WHERE d.organization_id = @OrganizationId AND d.id = @Id AND d.archived_at IS NULL;",
            new { OrganizationId = organizationId, Id = id }, cancellationToken: cancellationToken));
        if (row == null) return null;

        var widgets = await LoadWidgetsAsync(connection, organizationId, [row.Id], cancellationToken);
        return new ReportDashboardRecord(ToDashboardDto(row, widgets), row.OwnerUserId);
    }

    private static async Task<ILookup<Guid, WidgetRow>> LoadWidgetsAsync(
        IDbConnection connection, Guid organizationId, Guid[] dashboardIds, CancellationToken ct)
    {
        if (dashboardIds.Length == 0) return Enumerable.Empty<WidgetRow>().ToLookup(w => w.DashboardId);

        var widgets = await connection.QueryAsync<WidgetRow>(new CommandDefinition(@"
SELECT w.id AS Id, w.dashboard_id AS DashboardId, w.report_definition_id AS ReportDefinitionId, w.widget_type AS WidgetType,
       w.title AS Title, w.configuration_json AS ConfigurationJson, w.grid_x AS GridX, w.grid_y AS GridY,
       w.grid_w AS GridW, w.grid_h AS GridH, w.sort_order AS SortOrder
FROM dbo.nh_hr_report_dashboard_widgets w
WHERE w.organization_id = @OrganizationId AND w.dashboard_id IN @Ids
ORDER BY w.sort_order, w.created_at;", new { OrganizationId = organizationId, Ids = dashboardIds }, cancellationToken: ct));
        return widgets.ToLookup(w => w.DashboardId);
    }

    private static ReportDashboardDto ToDashboardDto(DashboardRow row, ILookup<Guid, WidgetRow> widgets) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Description = row.Description,
        Visibility = row.Visibility,
        IsDefault = row.IsDefault,
        GlobalFiltersJson = row.GlobalFiltersJson,
        LayoutJson = row.LayoutJson,
        CreatedAt = row.CreatedAt,
        Widgets = widgets[row.Id].Select(w => new ReportDashboardWidgetDto
        {
            Id = w.Id,
            ReportDefinitionId = w.ReportDefinitionId,
            WidgetType = w.WidgetType,
            Title = w.Title,
            ConfigurationJson = w.ConfigurationJson,
            GridX = w.GridX,
            GridY = w.GridY,
            GridW = w.GridW,
            GridH = w.GridH,
            SortOrder = w.SortOrder
        }).ToList()
    };

    public async Task<Guid> InsertDashboardAsync(
        Guid organizationId, Guid ownerUserId, ReportDashboardWrite row, IReadOnlyList<ReportDashboardWidgetWrite>? widgets,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();

        await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_report_dashboards
    (id, organization_id, name, description, owner_user_id, visibility, global_filters_json, layout_json, is_default, created_at, created_by)
VALUES
    (@Id, @OrganizationId, @Name, @Description, @OwnerUserId, @Visibility, @GlobalFiltersJson, @LayoutJson, @IsDefault, @Now, @OwnerUserId);",
            new
            {
                Id = id,
                OrganizationId = organizationId,
                OwnerUserId = ownerUserId,
                row.Name,
                row.Description,
                row.Visibility,
                row.GlobalFiltersJson,
                row.LayoutJson,
                row.IsDefault,
                Now = now
            }, tx, cancellationToken: cancellationToken));

        if (row.IsDefault)
            await ClearOtherDefaultsAsync(connection, tx, organizationId, ownerUserId, id, cancellationToken);

        if (widgets != null)
            await InsertWidgetsAsync(connection, tx, organizationId, id, widgets, now, cancellationToken);

        tx.Commit();
        return id;
    }

    public async Task<bool> UpdateDashboardAsync(
        Guid organizationId, Guid userId, Guid id, ReportDashboardWrite row, IReadOnlyList<ReportDashboardWidgetWrite>? widgets,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();

        var ownerId = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(@"
SELECT owner_user_id FROM dbo.nh_hr_report_dashboards
WHERE organization_id = @OrganizationId AND id = @Id AND archived_at IS NULL;",
            new { OrganizationId = organizationId, Id = id }, tx, cancellationToken: cancellationToken));
        if (!ownerId.HasValue) return false;

        await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_report_dashboards
SET name = @Name, description = @Description, visibility = @Visibility, global_filters_json = @GlobalFiltersJson,
    layout_json = @LayoutJson, is_default = @IsDefault, updated_at = @Now, updated_by = @UserId
WHERE organization_id = @OrganizationId AND id = @Id AND archived_at IS NULL;",
            new
            {
                OrganizationId = organizationId,
                Id = id,
                UserId = userId,
                row.Name,
                row.Description,
                row.Visibility,
                row.GlobalFiltersJson,
                row.LayoutJson,
                row.IsDefault,
                Now = now
            }, tx, cancellationToken: cancellationToken));

        if (row.IsDefault)
            await ClearOtherDefaultsAsync(connection, tx, organizationId, ownerId.Value, id, cancellationToken);

        if (widgets != null)
        {
            // Widgets are layout children of the dashboard: the set is replaced as a whole.
            await connection.ExecuteAsync(new CommandDefinition(@"
DELETE FROM dbo.nh_hr_report_dashboard_widgets
WHERE organization_id = @OrganizationId AND dashboard_id = @Id;",
                new { OrganizationId = organizationId, Id = id }, tx, cancellationToken: cancellationToken));
            await InsertWidgetsAsync(connection, tx, organizationId, id, widgets, now, cancellationToken);
        }

        tx.Commit();
        return true;
    }

    private static Task ClearOtherDefaultsAsync(
        IDbConnection connection, IDbTransaction tx, Guid organizationId, Guid ownerUserId, Guid keepId, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_report_dashboards SET is_default = 0
WHERE organization_id = @OrganizationId AND owner_user_id = @OwnerUserId AND id <> @KeepId
  AND archived_at IS NULL AND is_default = 1;",
            new { OrganizationId = organizationId, OwnerUserId = ownerUserId, KeepId = keepId }, tx, cancellationToken: ct));

    private static async Task InsertWidgetsAsync(
        IDbConnection connection, IDbTransaction tx, Guid organizationId, Guid dashboardId,
        IReadOnlyList<ReportDashboardWidgetWrite> widgets, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var w in widgets)
        {
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_report_dashboard_widgets
    (id, organization_id, dashboard_id, report_definition_id, widget_type, title, configuration_json,
     grid_x, grid_y, grid_w, grid_h, sort_order, created_at)
VALUES
    (@Id, @OrganizationId, @DashboardId, @ReportDefinitionId, @WidgetType, @Title, @ConfigurationJson,
     @GridX, @GridY, @GridW, @GridH, @SortOrder, @Now);",
                new
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = organizationId,
                    DashboardId = dashboardId,
                    w.ReportDefinitionId,
                    w.WidgetType,
                    w.Title,
                    w.ConfigurationJson,
                    w.GridX,
                    w.GridY,
                    w.GridW,
                    w.GridH,
                    w.SortOrder,
                    Now = now
                }, tx, cancellationToken: ct));
        }
    }

    public async Task<bool> ArchiveDashboardAsync(Guid organizationId, Guid userId, Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_report_dashboards
SET archived_at = @Now, is_default = 0, updated_at = @Now, updated_by = @UserId
WHERE organization_id = @OrganizationId AND id = @Id AND archived_at IS NULL;",
            new { OrganizationId = organizationId, Id = id, UserId = userId, Now = DateTimeOffset.UtcNow }, cancellationToken: cancellationToken));
        return affected > 0;
    }

    // -------------------------------------------------------------------------
    // Schedules
    // -------------------------------------------------------------------------

    private const string ScheduleSelectSql = @"
SELECT s.id AS Id, s.organization_id AS OrganizationId, s.report_definition_id AS ReportDefinitionId, r.name AS ReportName,
       s.owner_user_id AS OwnerUserId, s.name AS Name, s.frequency AS Frequency, s.schedule_json AS ScheduleJson,
       s.timezone AS Timezone, s.output_format AS OutputFormat, s.recipients_json AS RecipientsJson,
       s.is_active AS IsActive, s.next_run_at AS NextRunAt, s.last_run_at AS LastRunAt, s.last_status AS LastStatus
FROM dbo.nh_hr_report_schedules s
JOIN dbo.nh_hr_report_definitions r ON r.id = s.report_definition_id AND r.organization_id = s.organization_id";

    public async Task<IReadOnlyList<ReportScheduleRecord>> ListSchedulesAsync(Guid organizationId, Guid userId, bool seeAll, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var rows = await connection.QueryAsync<ReportScheduleRecord>(new CommandDefinition(ScheduleSelectSql + @"
WHERE s.organization_id = @OrganizationId AND s.archived_at IS NULL AND r.archived_at IS NULL
  AND (@SeeAll = 1 OR s.owner_user_id = @UserId)
ORDER BY s.name;",
            new { OrganizationId = organizationId, UserId = userId, SeeAll = seeAll }, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    public async Task<ReportScheduleRecord?> GetScheduleAsync(Guid organizationId, Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        return await connection.QuerySingleOrDefaultAsync<ReportScheduleRecord>(new CommandDefinition(ScheduleSelectSql + @"
WHERE s.organization_id = @OrganizationId AND s.id = @Id AND s.archived_at IS NULL;",
            new { OrganizationId = organizationId, Id = id }, cancellationToken: cancellationToken));
    }

    public async Task<Guid> InsertScheduleAsync(Guid organizationId, Guid ownerUserId, ReportScheduleWrite row, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_report_schedules
    (id, organization_id, report_definition_id, owner_user_id, name, frequency, schedule_json, timezone, output_format,
     recipients_json, is_active, next_run_at, created_at, created_by)
VALUES
    (@Id, @OrganizationId, @ReportDefinitionId, @OwnerUserId, @Name, @Frequency, @ScheduleJson, @Timezone, @OutputFormat,
     @RecipientsJson, @IsActive, @NextRunAt, @Now, @OwnerUserId);",
            new
            {
                Id = id,
                OrganizationId = organizationId,
                OwnerUserId = ownerUserId,
                row.ReportDefinitionId,
                row.Name,
                row.Frequency,
                row.ScheduleJson,
                row.Timezone,
                row.OutputFormat,
                row.RecipientsJson,
                row.IsActive,
                row.NextRunAt,
                Now = DateTimeOffset.UtcNow
            }, cancellationToken: cancellationToken));
        return id;
    }

    public async Task<bool> UpdateScheduleAsync(Guid organizationId, Guid userId, Guid id, ReportScheduleWrite row, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_report_schedules
SET report_definition_id = @ReportDefinitionId, name = @Name, frequency = @Frequency, schedule_json = @ScheduleJson,
    timezone = @Timezone, output_format = @OutputFormat, recipients_json = @RecipientsJson, is_active = @IsActive,
    next_run_at = @NextRunAt, updated_at = @Now, updated_by = @UserId
WHERE organization_id = @OrganizationId AND id = @Id AND archived_at IS NULL;",
            new
            {
                OrganizationId = organizationId,
                Id = id,
                UserId = userId,
                row.ReportDefinitionId,
                row.Name,
                row.Frequency,
                row.ScheduleJson,
                row.Timezone,
                row.OutputFormat,
                row.RecipientsJson,
                row.IsActive,
                row.NextRunAt,
                Now = DateTimeOffset.UtcNow
            }, cancellationToken: cancellationToken));
        return affected > 0;
    }

    public async Task<bool> ArchiveScheduleAsync(Guid organizationId, Guid userId, Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_report_schedules
SET archived_at = @Now, is_active = 0, updated_at = @Now, updated_by = @UserId
WHERE organization_id = @OrganizationId AND id = @Id AND archived_at IS NULL;",
            new { OrganizationId = organizationId, Id = id, UserId = userId, Now = DateTimeOffset.UtcNow }, cancellationToken: cancellationToken));
        return affected > 0;
    }

    public async Task<IReadOnlyList<ReportScheduleRecord>> ListDueSchedulesAsync(DateTimeOffset now, int take, CancellationToken cancellationToken = default)
    {
        // Intentionally spans tenants: the scheduler then acts strictly per schedule.OrganizationId.
        using var connection = OpenConnection();
        var rows = await connection.QueryAsync<ReportScheduleRecord>(new CommandDefinition(
            ScheduleSelectSql.Replace("SELECT s.id", "SELECT TOP (@Take) s.id") + @"
WHERE s.is_active = 1 AND s.archived_at IS NULL AND r.archived_at IS NULL
  AND s.next_run_at IS NOT NULL AND s.next_run_at <= @Now
ORDER BY s.next_run_at;",
            new { Now = now, Take = Math.Clamp(take, 1, 200) }, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    public async Task<bool> ClaimScheduleAsync(Guid organizationId, Guid id, DateTimeOffset now, DateTimeOffset nextRunAt, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_report_schedules
SET next_run_at = @NextRunAt, last_status = N'Running'
WHERE organization_id = @OrganizationId AND id = @Id AND is_active = 1 AND archived_at IS NULL
  AND next_run_at IS NOT NULL AND next_run_at <= @Now;",
            new { OrganizationId = organizationId, Id = id, Now = now, NextRunAt = nextRunAt }, cancellationToken: cancellationToken));
        return affected > 0;
    }

    public async Task MarkScheduleRunAsync(Guid organizationId, Guid id, DateTimeOffset lastRunAt, string status, DateTimeOffset? nextRunAt, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_report_schedules
SET last_run_at = @LastRunAt, last_status = @Status, next_run_at = COALESCE(@NextRunAt, next_run_at),
    updated_at = @LastRunAt
WHERE organization_id = @OrganizationId AND id = @Id;",
            new { OrganizationId = organizationId, Id = id, LastRunAt = lastRunAt, Status = status, NextRunAt = nextRunAt }, cancellationToken: cancellationToken));
    }

    // -------------------------------------------------------------------------
    // Executions
    // -------------------------------------------------------------------------

    private const string ExecutionSelectSql = @"
SELECT x.id AS Id, x.organization_id AS OrganizationId, x.report_definition_id AS ReportDefinitionId, x.schedule_id AS ScheduleId,
       x.requested_by AS RequestedBy, x.status AS Status, x.started_at AS StartedAt, x.completed_at AS CompletedAt,
       x.duration_ms AS DurationMs, x.row_count AS RowCount, x.output_format AS OutputFormat,
       x.nexa_document_id AS DocumentId, x.error_message AS ErrorMessage, x.expires_at AS ExpiresAt
FROM dbo.nh_hr_report_executions x";

    public async Task<Guid> InsertExecutionAsync(Guid organizationId, ReportExecutionInsert row, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_report_executions
    (id, organization_id, report_definition_id, schedule_id, requested_by, status, started_at, output_format, filters_json)
VALUES
    (@Id, @OrganizationId, @ReportDefinitionId, @ScheduleId, @RequestedBy, @Status, @Now, @OutputFormat, @FiltersJson);",
            new
            {
                Id = id,
                OrganizationId = organizationId,
                row.ReportDefinitionId,
                row.ScheduleId,
                row.RequestedBy,
                row.Status,
                row.OutputFormat,
                row.FiltersJson,
                Now = DateTimeOffset.UtcNow
            }, cancellationToken: cancellationToken));
        return id;
    }

    public async Task UpdateExecutionAsync(Guid organizationId, Guid id, ReportExecutionUpdate update, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_report_executions
SET status = @Status, completed_at = @CompletedAt, duration_ms = @DurationMs, row_count = @RowCount,
    error_code = @ErrorCode, error_message = @ErrorMessage, nexa_document_id = @DocumentId, expires_at = @ExpiresAt
WHERE organization_id = @OrganizationId AND id = @Id;",
            new
            {
                OrganizationId = organizationId,
                Id = id,
                update.Status,
                update.CompletedAt,
                update.DurationMs,
                update.RowCount,
                update.ErrorCode,
                ErrorMessage = Truncate(update.ErrorMessage, 1000),
                update.DocumentId,
                update.ExpiresAt
            }, cancellationToken: cancellationToken));
    }

    public async Task<ReportExecutionRecord?> GetExecutionAsync(Guid organizationId, Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        return await connection.QuerySingleOrDefaultAsync<ReportExecutionRecord>(new CommandDefinition(
            ExecutionSelectSql + " WHERE x.organization_id = @OrganizationId AND x.id = @Id;",
            new { OrganizationId = organizationId, Id = id }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<ReportExecutionRecord>> ListExecutionsByScheduleAsync(Guid organizationId, Guid scheduleId, int take, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var rows = await connection.QueryAsync<ReportExecutionRecord>(new CommandDefinition(
            ExecutionSelectSql.Replace("SELECT x.id", "SELECT TOP (@Take) x.id") + @"
WHERE x.organization_id = @OrganizationId AND x.schedule_id = @ScheduleId
ORDER BY x.started_at DESC;",
            new { OrganizationId = organizationId, ScheduleId = scheduleId, Take = Math.Clamp(take, 1, 200) }, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<ReportExecutionRecord>> ListExecutionsAsync(
        Guid organizationId, Guid? requestedBy, Guid? reportDefinitionId, int take, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var rows = await connection.QueryAsync<ReportExecutionRecord>(new CommandDefinition(
            ExecutionSelectSql.Replace("SELECT x.id", "SELECT TOP (@Take) x.id") + @"
WHERE x.organization_id = @OrganizationId
  AND (@RequestedBy IS NULL OR x.requested_by = @RequestedBy)
  AND (@ReportId IS NULL OR x.report_definition_id = @ReportId)
ORDER BY x.started_at DESC;",
            new { OrganizationId = organizationId, RequestedBy = requestedBy, ReportId = reportDefinitionId, Take = Math.Clamp(take, 1, 200) },
            cancellationToken: cancellationToken));
        return rows.ToList();
    }

    public async Task<bool> HasRecentExecutionAsync(Guid organizationId, Guid scheduleId, DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(1) FROM dbo.nh_hr_report_executions
WHERE organization_id = @OrganizationId AND schedule_id = @ScheduleId AND started_at >= @Since
  AND status IN (N'Pending', N'Running', N'Succeeded');",
            new { OrganizationId = organizationId, ScheduleId = scheduleId, Since = since }, cancellationToken: cancellationToken));
        return count > 0;
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

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int max) =>
        value == null ? null : (value.Length <= max ? value : value[..max]);

    private static bool IsDuplicateKey(SqlException ex) => ex.Number is 2627 or 2601;

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
            $"Core HR reports schema script not found. Expected sql/{SchemaScriptFileName}.");
    }
}
