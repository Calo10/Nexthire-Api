using System.Collections.Concurrent;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using nexthire_api.DTOs.CoreHr;
using nexthire_api.Exceptions;
using nexthire_api.Repositories;
using nexthire_api.Security;
using nexthire_api.Services.Reporting;

namespace nexthire_api.Services;

public class HrReportsService : IHrReportsService
{
    private const int ExportMaxRows = 10_000;
    private const int MaxRecipients = 25;
    private const int MaxJsonChars = 20_000;
    private const int DefaultRetentionDays = 7;
    private static readonly TimeSpan ScheduleDedupeWindow = TimeSpan.FromMinutes(5);

    private static readonly string[] Visibilities = ["Private", "SharedUsers", "SharedRoles", "Organization"];
    private static readonly string[] WidgetTypes = ["Chart", "Metric", "Table", "Text", "Insight"];
    private static readonly string[] EmploymentStatuses = ["Pending", "Onboarding", "Active", "OnLeave", "Inactive", "Terminated"];
    private static readonly IReadOnlySet<string> CsvExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".csv" };

    // Orgs whose system reports / development defaults were already ensured by this process.
    private static readonly ConcurrentDictionary<Guid, bool> OrgInitialized = new();

    private readonly IHrReportsRepository _repo;
    private readonly IEmployeeRepository _employees;
    private readonly IRoleRepository _roles;
    private readonly IResumeDocumentsUploader _uploader;
    private readonly IDocumentService _documents;
    private readonly IEmailService _email;
    private readonly IHostEnvironment _env;
    private readonly IConfiguration _configuration;
    private readonly ILogger<HrReportsService> _logger;

    public HrReportsService(
        IHrReportsRepository repo,
        IEmployeeRepository employees,
        IRoleRepository roles,
        IResumeDocumentsUploader uploader,
        IDocumentService documents,
        IEmailService email,
        IHostEnvironment env,
        IConfiguration configuration,
        ILogger<HrReportsService> logger)
    {
        _repo = repo;
        _employees = employees;
        _roles = roles;
        _uploader = uploader;
        _documents = documents;
        _email = email;
        _env = env;
        _configuration = configuration;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // Permissions / setup
    // -------------------------------------------------------------------------

    private Task<IReadOnlyList<string>> RolesAsync(HrActor actor) =>
        _roles.ListRoleCodesByNexaUserAsync(actor.OrganizationId, actor.UserId);

    private async Task<IReadOnlyList<string>> RequireAsync(HrActor actor, string capability)
    {
        var codes = await RolesAsync(actor);
        if (!ReportsPermissions.Has(codes, capability))
            throw new HrAccessDeniedException(capability);
        return codes;
    }

    /// <summary>Resolves a capability string from any Core HR permission family used by dataset/field gates.</summary>
    private static bool Can(IEnumerable<string> codes, string capability)
    {
        if (capability.StartsWith("Reports.", StringComparison.Ordinal)) return ReportsPermissions.Has(codes, capability);
        if (capability.StartsWith("Offboarding.", StringComparison.Ordinal)) return OffboardingPermissions.Has(codes, capability);
        return false; // unknown capability family: deny
    }

    private static Func<string, bool> PermissionCheck(IEnumerable<string> codes)
    {
        var list = codes.ToList();
        return capability => Can(list, capability);
    }

    /// <summary>System report cards for every org; in development also demo employees so analytics have data.</summary>
    private async Task EnsureOrgReadyAsync(HrActor actor, CancellationToken cancellationToken)
    {
        if (OrgInitialized.ContainsKey(actor.OrganizationId)) return;

        if (_env.IsDevelopment())
        {
            await _employees.EnsureOrgDefaultsAsync(actor.OrganizationId, cancellationToken);
            await _employees.SeedDemoEmployeesIfEmptyAsync(actor.OrganizationId, actor.UserId, true, cancellationToken);
        }

        await _repo.EnsureSystemReportsAsync(actor.OrganizationId, actor.UserId, cancellationToken);
        OrgInitialized[actor.OrganizationId] = true;
    }

    private static DateTime Today() => DateTime.UtcNow.Date;

    private static EmployeeServiceException Invalid(IEnumerable<string> errors) =>
        EmployeeServiceException.Validation(string.Join(" ", errors));

    private static HrReportsFilterQuery NormalizeFilter(HrReportsFilterQuery? filter)
    {
        var f = filter ?? new HrReportsFilterQuery();
        if (!string.IsNullOrWhiteSpace(f.EmploymentStatus))
        {
            var match = EmploymentStatuses.FirstOrDefault(s => s.Equals(f.EmploymentStatus.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match == null)
                throw EmployeeServiceException.Validation($"Unknown employment status '{f.EmploymentStatus.Trim()}'.");
            f.EmploymentStatus = match;
        }
        return f;
    }

    public async Task<IReadOnlyList<string>> GetCapabilitiesAsync(HrActor actor, CancellationToken cancellationToken = default)
    {
        var codes = await RolesAsync(actor);
        return ReportsPermissions.ListFor(codes);
    }

    // -------------------------------------------------------------------------
    // Analytics
    // -------------------------------------------------------------------------

    public async Task<HrReportsOverviewDto> GetOverviewAsync(HrActor actor, HrReportsFilterQuery? filter, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, ReportsPermissions.View);
        await EnsureOrgReadyAsync(actor, cancellationToken);

        var sections = HrOverviewSections.None;
        if (ReportsPermissions.Has(codes, ReportsPermissions.ViewOnboarding)) sections |= HrOverviewSections.Onboarding;
        if (ReportsPermissions.Has(codes, ReportsPermissions.ViewOffboarding)) sections |= HrOverviewSections.Offboarding;
        if (ReportsPermissions.Has(codes, ReportsPermissions.ViewCompliance)) sections |= HrOverviewSections.Documents;

        var overview = await _repo.GetOverviewAsync(actor.OrganizationId, NormalizeFilter(filter), Today(), sections, cancellationToken);

        var seeAll = ReportsPermissions.Has(codes, ReportsPermissions.ManageOrganizationReports);
        var reports = await _repo.ListReportsAsync(actor.OrganizationId, actor.UserId, seeAll, null, null, false, cancellationToken);
        overview.QuickReports = reports
            .Where(r => r.IsSystem && DatasetAllowed(codes, r.DatasetKey))
            .Take(6)
            .Select(r => new QuickReportDto
            {
                Key = r.Id.ToString(),
                Name = r.Name,
                Category = r.Category,
                Path = $"/hr/reports/{r.Id}"
            })
            .ToList();

        return overview;
    }

    public async Task<HrWorkforceAnalyticsDto> GetWorkforceAsync(HrActor actor, HrReportsFilterQuery? filter, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, ReportsPermissions.ViewWorkforce);
        return await _repo.GetWorkforceAsync(actor.OrganizationId, NormalizeFilter(filter), Today(), cancellationToken);
    }

    public async Task<HrRetentionAnalyticsDto> GetRetentionAsync(HrActor actor, HrReportsFilterQuery? filter, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, ReportsPermissions.ViewRetention);
        // Separation reasons are confidential offboarding information.
        var canSeeReasons = OffboardingPermissions.Has(codes, OffboardingPermissions.ViewConfidentialInformation);
        return await _repo.GetRetentionAsync(actor.OrganizationId, NormalizeFilter(filter), Today(), canSeeReasons, cancellationToken);
    }

    public async Task<HrOnboardingAnalyticsDto> GetOnboardingAsync(HrActor actor, HrReportsFilterQuery? filter, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, ReportsPermissions.ViewOnboarding);
        return await _repo.GetOnboardingAsync(actor.OrganizationId, NormalizeFilter(filter), Today(), cancellationToken);
    }

    public async Task<HrOffboardingAnalyticsDto> GetOffboardingAsync(HrActor actor, HrReportsFilterQuery? filter, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, ReportsPermissions.ViewOffboarding);
        return await _repo.GetOffboardingAsync(actor.OrganizationId, NormalizeFilter(filter), Today(), cancellationToken);
    }

    public async Task<HrOrganizationAnalyticsDto> GetOrganizationAsync(HrActor actor, HrReportsFilterQuery? filter, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, ReportsPermissions.ViewOrganization);
        return await _repo.GetOrganizationAsync(actor.OrganizationId, NormalizeFilter(filter), Today(), cancellationToken);
    }

    public async Task<HrDocumentsAnalyticsDto> GetDocumentsAsync(HrActor actor, HrReportsFilterQuery? filter, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, ReportsPermissions.ViewCompliance);
        return await _repo.GetDocumentsAsync(actor.OrganizationId, NormalizeFilter(filter), Today(), cancellationToken);
    }

    public async Task<HrCompensationAnalyticsDto> GetCompensationAsync(HrActor actor, HrReportsFilterQuery? filter, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, ReportsPermissions.ViewCompensation);
        return await _repo.GetCompensationAsync(actor.OrganizationId, NormalizeFilter(filter), Today(), cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Datasets
    // -------------------------------------------------------------------------

    private static bool DatasetAllowed(IEnumerable<string> codes, string datasetKey) =>
        ReportDatasetRegistry.Get(datasetKey) is { } ds && Can(codes, ds.RequiredPermission);

    public async Task<IReadOnlyList<ReportDatasetDto>> ListDatasetsAsync(HrActor actor, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, ReportsPermissions.View);
        var check = PermissionCheck(codes);
        return ReportDatasetRegistry.GetAll()
            .Where(d => check(d.RequiredPermission))
            .Select(d => ReportDatasetRegistry.ToDto(d, check))
            .ToList();
    }

    public async Task<ReportDatasetDto?> GetDatasetAsync(HrActor actor, string key, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, ReportsPermissions.View);
        var check = PermissionCheck(codes);
        var ds = ReportDatasetRegistry.Get(key);
        if (ds == null) return null;
        if (!check(ds.RequiredPermission)) throw new HrAccessDeniedException(ds.RequiredPermission);
        return ReportDatasetRegistry.ToDto(ds, check);
    }

    public async Task<IReadOnlyList<ReportFieldDto>?> GetDatasetFieldsAsync(HrActor actor, string key, CancellationToken cancellationToken = default) =>
        (await GetDatasetAsync(actor, key, cancellationToken))?.Fields;

    // -------------------------------------------------------------------------
    // Report definitions
    // -------------------------------------------------------------------------

    private static bool CanSeeReport(ReportDefinitionDto report, Guid userId, bool seeAll) =>
        report.IsSystem || seeAll || report.OwnerUserId == userId
        || !report.Visibility.Equals("Private", StringComparison.OrdinalIgnoreCase);

    /// <summary>Loads a report the caller may see and whose dataset they may use; otherwise null.</summary>
    private async Task<ReportDefinitionDto?> LoadVisibleReportAsync(HrActor actor, IReadOnlyList<string> codes, Guid id, CancellationToken ct)
    {
        var report = await _repo.GetReportAsync(actor.OrganizationId, actor.UserId, id, ct);
        if (report == null) return null;
        var seeAll = ReportsPermissions.Has(codes, ReportsPermissions.ManageOrganizationReports);
        if (!CanSeeReport(report, actor.UserId, seeAll)) return null;
        if (!DatasetAllowed(codes, report.DatasetKey)) return null;
        return report;
    }

    public async Task<IReadOnlyList<ReportDefinitionDto>> ListReportsAsync(
        HrActor actor, string? category, string? search, bool favoritesOnly, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, ReportsPermissions.View);
        await EnsureOrgReadyAsync(actor, cancellationToken);

        var seeAll = ReportsPermissions.Has(codes, ReportsPermissions.ManageOrganizationReports);
        var reports = await _repo.ListReportsAsync(actor.OrganizationId, actor.UserId, seeAll, category, search, favoritesOnly, cancellationToken);
        return reports.Where(r => DatasetAllowed(codes, r.DatasetKey)).ToList();
    }

    public async Task<ReportDefinitionDto?> GetReportAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, ReportsPermissions.View);
        await EnsureOrgReadyAsync(actor, cancellationToken);
        var report = await LoadVisibleReportAsync(actor, codes, id, cancellationToken);
        if (report != null) await TryRecordViewAsync(actor, id, cancellationToken);
        return report;
    }

    private async Task TryRecordViewAsync(HrActor actor, Guid reportId, CancellationToken ct)
    {
        try
        {
            await _repo.RecordViewAsync(actor.OrganizationId, actor.UserId, reportId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to record report view for report {ReportId}.", reportId);
        }
    }

    private ReportDefinitionWrite ValidateDefinition(
        IReadOnlyList<string> codes, CreateReportDefinitionRequest request, out ReportDatasetDef dataset)
    {
        var errors = new List<string>();

        var name = request.Name?.Trim() ?? "";
        if (name.Length == 0) errors.Add("Name is required.");
        else if (name.Length > 200) errors.Add("Name must be at most 200 characters.");

        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (description is { Length: > 1000 }) errors.Add("Description must be at most 1000 characters.");

        var category = SystemReportCatalog.Categories.FirstOrDefault(c => c.Equals(request.Category?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (category == null) errors.Add($"Category must be one of: {string.Join(", ", SystemReportCatalog.Categories)}.");

        var visibility = Visibilities.FirstOrDefault(v => v.Equals(request.Visibility?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (visibility == null) errors.Add($"Visibility must be one of: {string.Join(", ", Visibilities)}.");
        else if (visibility != "Private" && !ReportsPermissions.Has(codes, ReportsPermissions.Share))
            throw new HrAccessDeniedException(ReportsPermissions.Share);

        var ds = ReportDatasetRegistry.Get(request.DatasetKey);
        if (ds == null)
        {
            errors.Add($"Unknown dataset '{request.DatasetKey}'.");
        }
        else if (!Can(codes, ds.RequiredPermission))
        {
            throw new HrAccessDeniedException(ds.RequiredPermission);
        }

        var configJson = string.IsNullOrWhiteSpace(request.ConfigurationJson) ? "{}" : request.ConfigurationJson.Trim();
        if (ds != null)
        {
            var validation = ReportDatasetRegistry.ValidateConfiguration(ds, configJson, PermissionCheck(codes));
            if (!validation.IsValid) errors.AddRange(validation.Errors);
        }

        string? visualization = null;
        if (!string.IsNullOrWhiteSpace(request.VisualizationJson))
        {
            visualization = request.VisualizationJson.Trim();
            if (!IsValidJson(visualization)) errors.Add("Visualization must be valid JSON (max 20000 characters).");
        }

        if (errors.Count > 0) throw Invalid(errors);

        dataset = ds!;
        return new ReportDefinitionWrite(name, description, category!, ds!.Key, configJson, visualization, visibility!);
    }

    public async Task<ReportDefinitionDto> CreateReportAsync(HrActor actor, CreateReportDefinitionRequest request, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, ReportsPermissions.Create);
        var row = ValidateDefinition(codes, request, out _);
        var id = await _repo.InsertReportAsync(actor.OrganizationId, actor.UserId, row, cancellationToken);
        return await _repo.GetReportAsync(actor.OrganizationId, actor.UserId, id, cancellationToken)
               ?? throw EmployeeServiceException.NotFound("Report was created but could not be loaded.");
    }

    public async Task<ReportDefinitionDto?> UpdateReportAsync(HrActor actor, Guid id, UpdateReportDefinitionRequest request, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, ReportsPermissions.Edit);
        var existing = await LoadVisibleReportAsync(actor, codes, id, cancellationToken);
        if (existing == null) return null;

        if (existing.IsSystem)
            throw EmployeeServiceException.Validation("System reports cannot be modified. Duplicate the report to customize it.");
        if (existing.OwnerUserId != actor.UserId && !ReportsPermissions.Has(codes, ReportsPermissions.ManageOrganizationReports))
            throw new HrAccessDeniedException(ReportsPermissions.ManageOrganizationReports);

        var row = ValidateDefinition(codes, request, out _);
        if (!await _repo.UpdateReportAsync(actor.OrganizationId, actor.UserId, id, row, cancellationToken)) return null;
        return await _repo.GetReportAsync(actor.OrganizationId, actor.UserId, id, cancellationToken);
    }

    public async Task<ReportDefinitionDto?> DuplicateReportAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, ReportsPermissions.Create);
        var source = await LoadVisibleReportAsync(actor, codes, id, cancellationToken);
        if (source == null) return null;

        var copyName = $"{source.Name} (copy)";
        if (copyName.Length > 200) copyName = copyName[..200];
        var copyId = await _repo.DuplicateReportAsync(actor.OrganizationId, actor.UserId, id, copyName, cancellationToken);
        return copyId == null ? null : await _repo.GetReportAsync(actor.OrganizationId, actor.UserId, copyId.Value, cancellationToken);
    }

    public async Task<bool> ArchiveReportAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, ReportsPermissions.Edit);
        var existing = await LoadVisibleReportAsync(actor, codes, id, cancellationToken);
        if (existing == null) return false;

        if (existing.IsSystem)
            throw EmployeeServiceException.Validation("System reports cannot be archived.");
        if (existing.OwnerUserId != actor.UserId && !ReportsPermissions.Has(codes, ReportsPermissions.ManageOrganizationReports))
            throw new HrAccessDeniedException(ReportsPermissions.ManageOrganizationReports);

        return await _repo.ArchiveReportAsync(actor.OrganizationId, actor.UserId, id, cancellationToken);
    }

    public async Task<bool> SetFavoriteAsync(HrActor actor, Guid id, bool isFavorite, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, ReportsPermissions.View);
        var report = await LoadVisibleReportAsync(actor, codes, id, cancellationToken);
        if (report == null) return false;
        await _repo.SetFavoriteAsync(actor.OrganizationId, actor.UserId, id, isFavorite, cancellationToken);
        return true;
    }

    // -------------------------------------------------------------------------
    // Run (preview / execute / export)
    // -------------------------------------------------------------------------

    private sealed record QueryRun(ReportQueryResult Result, ReportExecutionResultDto Dto);

    /// <summary>Validates the configuration for the caller and returns the dataset + parsed configuration.</summary>
    private static (ReportDatasetDef Dataset, ReportConfiguration Config) ResolveConfiguration(
        IReadOnlyList<string> codes, string datasetKey, string? configurationJson)
    {
        var ds = ReportDatasetRegistry.Get(datasetKey)
                 ?? throw EmployeeServiceException.Validation($"Unknown dataset '{datasetKey}'.");
        if (!Can(codes, ds.RequiredPermission)) throw new HrAccessDeniedException(ds.RequiredPermission);

        var validation = ReportDatasetRegistry.ValidateConfiguration(ds, configurationJson, PermissionCheck(codes));
        if (!validation.IsValid) throw Invalid(validation.Errors);
        return (ds, validation.Configuration!);
    }

    private async Task<QueryRun> RunQueryAsync(
        ReportDatasetDef dataset, ReportConfiguration config, ReportQueryContext context, CancellationToken ct)
    {
        var query = ReportQueryBuilder.Build(dataset, config, context);

        ReportQueryResult result;
        try
        {
            result = await _repo.ExecuteReportQueryAsync(query, ct);
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "Report query failed for dataset {Dataset} (organization {OrganizationId}).", dataset.Key, context.OrganizationId);
            throw new EmployeeServiceException(StatusCodes.Status500InternalServerError, "report_failed", "The report could not be generated. Try narrowing the filters.");
        }

        var dto = new ReportExecutionResultDto
        {
            DatasetKey = dataset.Key,
            Columns = result.Columns.Select(c => c.Key).ToList(),
            Rows = result.Rows,
            RowCount = result.Rows.Count,
            TruncatedAt = result.Truncated ? result.RowLimit : 0,
            Series = BuildSeries(config, result)
        };
        return new QueryRun(result, dto);
    }

    /// <summary>Chart-friendly series for aggregated reports: first measure by the dimension value(s).</summary>
    private static IReadOnlyList<NamedCountDto>? BuildSeries(ReportConfiguration config, ReportQueryResult result)
    {
        if (!config.IsAggregated || config.Dimensions.Count == 0) return null;

        var measureKey = config.Measures[0].OutputKey;
        var series = new List<NamedCountDto>();
        foreach (var row in result.Rows.Take(50))
        {
            var label = string.Join(" / ", config.Dimensions.Select(d => Convert.ToString(row.GetValueOrDefault(d), CultureInfo.InvariantCulture) ?? ""));
            var value = row.TryGetValue(measureKey, out var raw) && raw != null
                ? Convert.ToDouble(raw, CultureInfo.InvariantCulture)
                : 0;
            series.Add(new NamedCountDto { Key = label, Label = label, Value = value });
        }

        var total = series.Sum(s => s.Value);
        if (config.Measures[0].Agg is ReportAggregations.Count or ReportAggregations.DistinctCount or ReportAggregations.Sum && total > 0)
        {
            foreach (var s in series)
                s.Percent = Math.Round(s.Value * 100.0 / total, 1);
        }
        return series;
    }

    private static ReportQueryContext ContextFor(HrActor actor, ReportPreviewRequest? request, int? maxRows = null)
    {
        int? rows = maxRows;
        if (request?.Limit is { } limit)
            rows = Math.Clamp(limit, 1, maxRows ?? ReportDatasetRegistry.MaxLimit);

        return new ReportQueryContext(actor.OrganizationId, Today(), request?.From, request?.To, request?.DepartmentId, rows);
    }

    public async Task<ReportExecutionResultDto> PreviewAsync(HrActor actor, ReportPreviewRequest request, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, ReportsPermissions.View);
        var (ds, config) = ResolveConfiguration(codes, request.DatasetKey, request.ConfigurationJson);
        var run = await RunQueryAsync(ds, config, ContextFor(actor, request), cancellationToken);
        return run.Dto;
    }

    public async Task<ReportExecutionResultDto?> ExecuteAsync(HrActor actor, Guid id, ReportPreviewRequest? overrides, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, ReportsPermissions.View);
        await EnsureOrgReadyAsync(actor, cancellationToken);
        var report = await LoadVisibleReportAsync(actor, codes, id, cancellationToken);
        if (report == null) return null;

        var (ds, config) = ResolveConfiguration(codes, report.DatasetKey, report.ConfigurationJson);
        var execId = await _repo.InsertExecutionAsync(actor.OrganizationId,
            new ReportExecutionInsert(id, null, actor.UserId, "Running", null, FiltersJson(overrides)), cancellationToken);

        var sw = Stopwatch.StartNew();
        try
        {
            var run = await RunQueryAsync(ds, config, ContextFor(actor, overrides), cancellationToken);
            run.Dto.ExecutionId = execId;
            await _repo.UpdateExecutionAsync(actor.OrganizationId, execId,
                new ReportExecutionUpdate("Succeeded", DateTimeOffset.UtcNow, (int)sw.ElapsedMilliseconds, run.Dto.RowCount, null, null, null, null),
                cancellationToken);
            await TryRecordViewAsync(actor, id, cancellationToken);
            return run.Dto;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await TryMarkFailedAsync(actor.OrganizationId, execId, sw, ex, cancellationToken);
            throw;
        }
    }

    public async Task<ReportExportOutcome?> ExportAsync(HrActor actor, Guid id, ReportExportRequest request, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, ReportsPermissions.Export);
        if (!ReportsPermissions.Has(codes, ReportsPermissions.View)) throw new HrAccessDeniedException(ReportsPermissions.View);

        var format = string.IsNullOrWhiteSpace(request.Format) ? "Csv" : request.Format.Trim();
        if (!format.Equals("Csv", StringComparison.OrdinalIgnoreCase))
            throw EmployeeServiceException.Validation("Only Csv export is currently supported.");

        await EnsureOrgReadyAsync(actor, cancellationToken);
        var report = await LoadVisibleReportAsync(actor, codes, id, cancellationToken);
        if (report == null) return null;

        var (ds, config) = ResolveConfiguration(codes, report.DatasetKey,
            string.IsNullOrWhiteSpace(request.ConfigurationJson) ? report.ConfigurationJson : request.ConfigurationJson);

        var execId = await _repo.InsertExecutionAsync(actor.OrganizationId,
            new ReportExecutionInsert(id, null, actor.UserId, "Running", "Csv", null), cancellationToken);

        var sw = Stopwatch.StartNew();
        try
        {
            var outcome = await GenerateExportAsync(actor.OrganizationId, execId, report.Name, ds, config,
                new ReportQueryContext(actor.OrganizationId, Today(), MaxRowsOverride: ExportMaxRows), sw, cancellationToken);
            return outcome;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await TryMarkFailedAsync(actor.OrganizationId, execId, sw, ex, cancellationToken);
            throw;
        }
    }

    /// <summary>Runs the query, builds the CSV, stores it when possible and finalizes the execution row.</summary>
    private async Task<ReportExportOutcome> GenerateExportAsync(
        Guid organizationId, Guid executionId, string reportName, ReportDatasetDef dataset, ReportConfiguration config,
        ReportQueryContext context, Stopwatch sw, CancellationToken ct)
    {
        var run = await RunQueryAsync(dataset, config, context, ct);
        var content = BuildCsv(run.Result);
        var fileName = $"{Slug(reportName)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";

        var documentId = await TryUploadAsync(organizationId, content, fileName, ct);
        var retentionDays = Math.Clamp(_configuration.GetValue<int?>("Reports:ExportRetentionDays") ?? DefaultRetentionDays, 1, 90);
        var expiresAt = DateTimeOffset.UtcNow.AddDays(retentionDays);

        await _repo.UpdateExecutionAsync(organizationId, executionId,
            new ReportExecutionUpdate("Succeeded", DateTimeOffset.UtcNow, (int)sw.ElapsedMilliseconds, run.Dto.RowCount, null, null,
                documentId, expiresAt), ct);

        var result = new ReportExportResultDto
        {
            ExecutionId = executionId,
            Format = "Csv",
            DocumentId = documentId,
            DownloadPath = documentId.HasValue ? $"/api/documents/{documentId.Value}/download-url" : null,
            RowCount = run.Dto.RowCount,
            ExpiresAt = expiresAt
        };
        return new ReportExportOutcome(result, content, fileName, "text/csv; charset=utf-8");
    }

    /// <summary>Stores the CSV through the Documents service. Failure is non-fatal: the caller still gets the bytes.</summary>
    private async Task<Guid?> TryUploadAsync(Guid organizationId, byte[] content, string fileName, CancellationToken ct)
    {
        try
        {
            await using var stream = new MemoryStream(content, writable: false);
            var uploaded = await _uploader.UploadDocumentAsync(organizationId, stream, fileName, "text/csv", content.Length, CsvExtensions, ct);
            return Guid.TryParse(uploaded, out var id) ? id : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Report export could not be stored in Documents for organization {OrganizationId}; continuing without a stored file.", organizationId);
            return null;
        }
    }

    private async Task TryMarkFailedAsync(Guid organizationId, Guid executionId, Stopwatch sw, Exception ex, CancellationToken ct)
    {
        try
        {
            var message = ex is EmployeeServiceException ? ex.Message : "The report could not be generated.";
            await _repo.UpdateExecutionAsync(organizationId, executionId,
                new ReportExecutionUpdate("Failed", DateTimeOffset.UtcNow, (int)sw.ElapsedMilliseconds, null,
                    ex is EmployeeServiceException ese ? ese.Code : "report_failed", message, null, null),
                CancellationToken.None);
        }
        catch (Exception inner) when (inner is not OperationCanceledException)
        {
            _logger.LogWarning(inner, "Failed to record failed report execution {ExecutionId}.", executionId);
        }
    }

    private static string? FiltersJson(ReportPreviewRequest? request) =>
        request == null || (request.From == null && request.To == null && request.DepartmentId == null && request.Limit == null)
            ? null
            : JsonSerializer.Serialize(new { request.From, request.To, request.DepartmentId, request.Limit });

    // ---- CSV -----------------------------------------------------------------

    private static byte[] BuildCsv(ReportQueryResult result)
    {
        var sb = new StringBuilder();
        sb.Append('\uFEFF');
        sb.Append(string.Join(",", result.Columns.Select(c => CsvCell(c.Label))));
        sb.Append("\r\n");

        foreach (var row in result.Rows)
        {
            sb.Append(string.Join(",", result.Columns.Select(c => CsvCell(row.TryGetValue(c.Key, out var v) ? v : null))));
            sb.Append("\r\n");
        }

        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }

    private static string CsvCell(object? value)
    {
        string text;
        switch (value)
        {
            case null: return "";
            case bool b: text = b ? "true" : "false"; break;
            case double d: text = d.ToString("0.##", CultureInfo.InvariantCulture); break;
            case decimal m: text = m.ToString("0.##", CultureInfo.InvariantCulture); break;
            case IFormattable f: text = f.ToString(null, CultureInfo.InvariantCulture); break;
            default:
                text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                // Spreadsheet formula injection guard (text only; numbers keep their sign).
                if (text.Length > 0 && "=+-@\t\r".Contains(text[0])) text = "'" + text;
                break;
        }

        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }

    private static string Slug(string name)
    {
        var slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length == 0) slug = "report";
        return slug.Length > 60 ? slug[..60].Trim('-') : slug;
    }

    // -------------------------------------------------------------------------
    // Dashboards
    // -------------------------------------------------------------------------

    private static bool CanSeeDashboard(ReportDashboardRecord record, Guid userId, bool seeAll) =>
        seeAll || record.OwnerUserId == userId
        || !record.Dashboard.Visibility.Equals("Private", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<ReportDashboardDto>> ListDashboardsAsync(HrActor actor, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, ReportsPermissions.View);
        var seeAll = ReportsPermissions.Has(codes, ReportsPermissions.ManageOrganizationReports);
        return await _repo.ListDashboardsAsync(actor.OrganizationId, actor.UserId, seeAll, cancellationToken);
    }

    public async Task<ReportDashboardDto?> GetDashboardAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, ReportsPermissions.View);
        var record = await _repo.GetDashboardAsync(actor.OrganizationId, id, cancellationToken);
        if (record == null) return null;
        var seeAll = ReportsPermissions.Has(codes, ReportsPermissions.ManageOrganizationReports);
        return CanSeeDashboard(record, actor.UserId, seeAll) ? record.Dashboard : null;
    }

    private async Task<(ReportDashboardWrite Row, List<ReportDashboardWidgetWrite>? Widgets)> ValidateDashboardAsync(
        HrActor actor, IReadOnlyList<string> codes, UpsertReportDashboardRequest request, CancellationToken ct)
    {
        var errors = new List<string>();

        var name = request.Name?.Trim() ?? "";
        if (name.Length == 0) errors.Add("Name is required.");
        else if (name.Length > 200) errors.Add("Name must be at most 200 characters.");

        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (description is { Length: > 1000 }) errors.Add("Description must be at most 1000 characters.");

        var visibility = Visibilities.FirstOrDefault(v => v.Equals(request.Visibility?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (visibility == null) errors.Add($"Visibility must be one of: {string.Join(", ", Visibilities)}.");
        else if (visibility != "Private" && !ReportsPermissions.Has(codes, ReportsPermissions.Share))
            throw new HrAccessDeniedException(ReportsPermissions.Share);

        string? globalFilters = string.IsNullOrWhiteSpace(request.GlobalFiltersJson) ? null : request.GlobalFiltersJson.Trim();
        if (globalFilters != null && !IsValidJson(globalFilters)) errors.Add("Global filters must be valid JSON (max 20000 characters).");
        string? layout = string.IsNullOrWhiteSpace(request.LayoutJson) ? null : request.LayoutJson.Trim();
        if (layout != null && !IsValidJson(layout)) errors.Add("Layout must be valid JSON (max 20000 characters).");

        List<ReportDashboardWidgetWrite>? widgets = null;
        if (request.Widgets != null)
        {
            if (request.Widgets.Count > 50) errors.Add("A dashboard can have at most 50 widgets.");
            else
            {
                widgets = [];
                var seeAll = ReportsPermissions.Has(codes, ReportsPermissions.ManageOrganizationReports);
                var checkedReports = new Dictionary<Guid, bool>();

                foreach (var w in request.Widgets)
                {
                    var type = WidgetTypes.FirstOrDefault(t => t.Equals(w.WidgetType?.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (type == null)
                    {
                        errors.Add($"Widget type must be one of: {string.Join(", ", WidgetTypes)}.");
                        continue;
                    }

                    var config = string.IsNullOrWhiteSpace(w.ConfigurationJson) ? "{}" : w.ConfigurationJson.Trim();
                    if (!IsValidJson(config)) errors.Add("Widget configuration must be valid JSON (max 20000 characters).");

                    var title = string.IsNullOrWhiteSpace(w.Title) ? null : w.Title.Trim();
                    if (title is { Length: > 200 }) errors.Add("Widget title must be at most 200 characters.");

                    if (w.GridX < 0 || w.GridY < 0 || w.GridW is < 1 or > 12 || w.GridH is < 1 or > 24)
                        errors.Add("Widget grid position/size is out of range (w 1-12, h 1-24).");

                    if (w.ReportDefinitionId.HasValue)
                    {
                        var rid = w.ReportDefinitionId.Value;
                        if (!checkedReports.TryGetValue(rid, out var ok))
                        {
                            var report = await LoadVisibleReportAsync(actor, codes, rid, ct);
                            ok = report != null && (report.IsSystem || seeAll || report.OwnerUserId == actor.UserId || report.Visibility != "Private");
                            checkedReports[rid] = ok;
                        }
                        if (!ok) errors.Add($"Report {rid} was not found.");
                    }

                    widgets.Add(new ReportDashboardWidgetWrite(w.ReportDefinitionId, type, title, config, w.GridX, w.GridY, w.GridW, w.GridH, w.SortOrder));
                }
            }
        }

        if (errors.Count > 0) throw Invalid(errors.Distinct());

        return (new ReportDashboardWrite(name, description, visibility!, request.IsDefault, globalFilters, layout), widgets);
    }

    public async Task<ReportDashboardDto> CreateDashboardAsync(HrActor actor, UpsertReportDashboardRequest request, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, ReportsPermissions.ManageDashboards);
        var (row, widgets) = await ValidateDashboardAsync(actor, codes, request, cancellationToken);
        var id = await _repo.InsertDashboardAsync(actor.OrganizationId, actor.UserId, row, widgets, cancellationToken);
        return (await _repo.GetDashboardAsync(actor.OrganizationId, id, cancellationToken))?.Dashboard
               ?? throw EmployeeServiceException.NotFound("Dashboard was created but could not be loaded.");
    }

    public async Task<ReportDashboardDto?> UpdateDashboardAsync(HrActor actor, Guid id, UpsertReportDashboardRequest request, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, ReportsPermissions.ManageDashboards);
        var existing = await _repo.GetDashboardAsync(actor.OrganizationId, id, cancellationToken);
        if (existing == null) return null;

        var manageAll = ReportsPermissions.Has(codes, ReportsPermissions.ManageOrganizationReports);
        if (!CanSeeDashboard(existing, actor.UserId, manageAll)) return null;
        if (existing.OwnerUserId != actor.UserId && !manageAll)
            throw new HrAccessDeniedException(ReportsPermissions.ManageOrganizationReports);

        var (row, widgets) = await ValidateDashboardAsync(actor, codes, request, cancellationToken);
        if (!await _repo.UpdateDashboardAsync(actor.OrganizationId, actor.UserId, id, row, widgets, cancellationToken)) return null;
        return (await _repo.GetDashboardAsync(actor.OrganizationId, id, cancellationToken))?.Dashboard;
    }

    public async Task<bool> ArchiveDashboardAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, ReportsPermissions.ManageDashboards);
        var existing = await _repo.GetDashboardAsync(actor.OrganizationId, id, cancellationToken);
        if (existing == null) return false;

        var manageAll = ReportsPermissions.Has(codes, ReportsPermissions.ManageOrganizationReports);
        if (!CanSeeDashboard(existing, actor.UserId, manageAll)) return false;
        if (existing.OwnerUserId != actor.UserId && !manageAll)
            throw new HrAccessDeniedException(ReportsPermissions.ManageOrganizationReports);

        return await _repo.ArchiveDashboardAsync(actor.OrganizationId, actor.UserId, id, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Schedules
    // -------------------------------------------------------------------------

    private static ReportScheduleDto ToDto(ReportScheduleRecord s) => new()
    {
        Id = s.Id,
        ReportDefinitionId = s.ReportDefinitionId,
        ReportName = s.ReportName,
        Name = s.Name,
        Frequency = s.Frequency,
        Timezone = s.Timezone,
        OutputFormat = s.OutputFormat,
        RecipientsJson = s.RecipientsJson,
        IsActive = s.IsActive,
        NextRunAt = s.NextRunAt,
        LastRunAt = s.LastRunAt,
        LastStatus = s.LastStatus
    };

    public async Task<IReadOnlyList<ReportScheduleDto>> ListSchedulesAsync(HrActor actor, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, ReportsPermissions.Schedule);
        var seeAll = ReportsPermissions.Has(codes, ReportsPermissions.ManageOrganizationReports);
        var rows = await _repo.ListSchedulesAsync(actor.OrganizationId, actor.UserId, seeAll, cancellationToken);
        return rows.Select(ToDto).ToList();
    }

    public async Task<ReportScheduleDto?> GetScheduleAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, ReportsPermissions.Schedule);
        var record = await _repo.GetScheduleAsync(actor.OrganizationId, id, cancellationToken);
        if (record == null) return null;
        var seeAll = ReportsPermissions.Has(codes, ReportsPermissions.ManageOrganizationReports);
        return record.OwnerUserId == actor.UserId || seeAll ? ToDto(record) : null;
    }

    private async Task<(ReportScheduleWrite Row, string NormalizedFrequency)> ValidateScheduleAsync(
        HrActor actor, IReadOnlyList<string> codes, UpsertReportScheduleRequest request, CancellationToken ct)
    {
        var errors = new List<string>();

        var name = request.Name?.Trim() ?? "";
        if (name.Length == 0) errors.Add("Name is required.");
        else if (name.Length > 200) errors.Add("Name must be at most 200 characters.");

        var frequency = ReportScheduleCalculator.NormalizeFrequency(request.Frequency);
        if (frequency == null) errors.Add($"Frequency must be one of: {string.Join(", ", ReportScheduleCalculator.Frequencies)}.");

        if (!"Csv".Equals(request.OutputFormat?.Trim(), StringComparison.OrdinalIgnoreCase))
            errors.Add("Only Csv output is currently supported for schedules.");

        var timezone = request.Timezone?.Trim() ?? "";
        if (!ReportScheduleCalculator.IsKnownTimeZone(timezone)) errors.Add("Unknown timezone.");

        var scheduleError = ReportScheduleCalculator.ValidateScheduleJson(request.ScheduleJson);
        if (scheduleError != null) errors.Add(scheduleError);

        var recipients = ParseRecipients(request.RecipientsJson, out var recipientError);
        if (recipientError != null) errors.Add(recipientError);

        var report = await LoadVisibleReportAsync(actor, codes, request.ReportDefinitionId, ct);
        if (report == null) errors.Add("Report was not found.");

        if (errors.Count > 0) throw Invalid(errors);

        var next = request.NextRunAt is { } requested && requested > DateTimeOffset.UtcNow
            ? requested
            : ReportScheduleCalculator.ComputeNextRun(frequency!, request.ScheduleJson, timezone, DateTimeOffset.UtcNow);

        var row = new ReportScheduleWrite(
            request.ReportDefinitionId, name, frequency!,
            string.IsNullOrWhiteSpace(request.ScheduleJson) ? null : request.ScheduleJson.Trim(),
            timezone, "Csv", JsonSerializer.Serialize(recipients), request.IsActive, next);
        return (row, frequency!);
    }

    public async Task<ReportScheduleDto> CreateScheduleAsync(HrActor actor, UpsertReportScheduleRequest request, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, ReportsPermissions.Schedule);
        if (!ReportsPermissions.Has(codes, ReportsPermissions.Export)) throw new HrAccessDeniedException(ReportsPermissions.Export);

        var (row, _) = await ValidateScheduleAsync(actor, codes, request, cancellationToken);
        var id = await _repo.InsertScheduleAsync(actor.OrganizationId, actor.UserId, row, cancellationToken);
        return ToDto(await _repo.GetScheduleAsync(actor.OrganizationId, id, cancellationToken)
                     ?? throw EmployeeServiceException.NotFound("Schedule was created but could not be loaded."));
    }

    public async Task<ReportScheduleDto?> UpdateScheduleAsync(HrActor actor, Guid id, UpsertReportScheduleRequest request, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, ReportsPermissions.Schedule);
        var existing = await _repo.GetScheduleAsync(actor.OrganizationId, id, cancellationToken);
        if (existing == null) return null;

        var manageAll = ReportsPermissions.Has(codes, ReportsPermissions.ManageOrganizationReports);
        if (existing.OwnerUserId != actor.UserId && !manageAll)
            throw new HrAccessDeniedException(ReportsPermissions.ManageOrganizationReports);

        var (row, _) = await ValidateScheduleAsync(actor, codes, request, cancellationToken);
        if (!await _repo.UpdateScheduleAsync(actor.OrganizationId, actor.UserId, id, row, cancellationToken)) return null;
        var updated = await _repo.GetScheduleAsync(actor.OrganizationId, id, cancellationToken);
        return updated == null ? null : ToDto(updated);
    }

    public async Task<bool> ArchiveScheduleAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, ReportsPermissions.Schedule);
        var existing = await _repo.GetScheduleAsync(actor.OrganizationId, id, cancellationToken);
        if (existing == null) return false;

        var manageAll = ReportsPermissions.Has(codes, ReportsPermissions.ManageOrganizationReports);
        if (existing.OwnerUserId != actor.UserId && !manageAll)
            throw new HrAccessDeniedException(ReportsPermissions.ManageOrganizationReports);

        return await _repo.ArchiveScheduleAsync(actor.OrganizationId, actor.UserId, id, cancellationToken);
    }

    public async Task<IReadOnlyList<ReportExecutionDto>?> GetScheduleHistoryAsync(HrActor actor, Guid id, int take, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, ReportsPermissions.ViewExecutionHistory);
        var schedule = await _repo.GetScheduleAsync(actor.OrganizationId, id, cancellationToken);
        if (schedule == null) return null;

        var manageAll = ReportsPermissions.Has(codes, ReportsPermissions.ManageOrganizationReports);
        if (schedule.OwnerUserId != actor.UserId && !manageAll) return null;

        var rows = await _repo.ListExecutionsByScheduleAsync(actor.OrganizationId, id, take <= 0 ? 25 : take, cancellationToken);
        return rows.Select(r => r.ToDto()).ToList();
    }

    /// <summary>Accepts a JSON array of email strings (or objects with an <c>email</c> property).</summary>
    private static List<string> ParseRecipients(string? json, out string? error)
    {
        error = null;
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "At least one recipient is required.";
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "Recipients must be a JSON array of email addresses.";
                return result;
            }

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                string? email = item.ValueKind switch
                {
                    JsonValueKind.String => item.GetString(),
                    JsonValueKind.Object when item.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String => e.GetString(),
                    _ => null
                };

                email = email?.Trim();
                if (!IsValidEmail(email))
                {
                    error = "Recipients contain an invalid email address.";
                    return [];
                }
                if (!result.Contains(email!, StringComparer.OrdinalIgnoreCase)) result.Add(email!);
            }
        }
        catch (JsonException)
        {
            error = "Recipients must be valid JSON.";
            return [];
        }

        if (result.Count == 0) error = "At least one recipient is required.";
        else if (result.Count > MaxRecipients) error = $"At most {MaxRecipients} recipients are allowed.";
        return result;
    }

    private static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 256 || email.Any(char.IsWhiteSpace)) return false;
        try
        {
            var addr = new MailAddress(email);
            return addr.Address.Equals(email, StringComparison.OrdinalIgnoreCase) && email.Contains('@');
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Executions
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<ReportExecutionDto>> ListExecutionsAsync(HrActor actor, Guid? reportId, int take, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, ReportsPermissions.View);
        var seeAll = ReportsPermissions.Has(codes, ReportsPermissions.ViewExecutionHistory);
        var rows = await _repo.ListExecutionsAsync(actor.OrganizationId, seeAll ? null : actor.UserId, reportId, take <= 0 ? 25 : take, cancellationToken);
        return rows.Select(r => r.ToDto()).ToList();
    }

    private async Task<ReportExecutionRecord?> LoadVisibleExecutionAsync(HrActor actor, IReadOnlyList<string> codes, Guid id, CancellationToken ct)
    {
        var record = await _repo.GetExecutionAsync(actor.OrganizationId, id, ct);
        if (record == null) return null;
        var canSeeAll = ReportsPermissions.Has(codes, ReportsPermissions.ViewExecutionHistory)
                        || ReportsPermissions.Has(codes, ReportsPermissions.ManageOrganizationReports);
        return record.RequestedBy == actor.UserId || canSeeAll ? record : null;
    }

    public async Task<ReportExecutionDto?> GetExecutionAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, ReportsPermissions.View);
        return (await LoadVisibleExecutionAsync(actor, codes, id, cancellationToken))?.ToDto();
    }

    public async Task<ReportExecutionDownloadDto?> GetExecutionDownloadAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, ReportsPermissions.Export);
        var record = await LoadVisibleExecutionAsync(actor, codes, id, cancellationToken);
        if (record == null) return null;

        if (record.ExpiresAt is { } expires && expires < DateTimeOffset.UtcNow)
            throw new EmployeeServiceException(StatusCodes.Status410Gone, "expired", "This export has expired. Run the export again.");
        if (record.DocumentId is not { } documentId)
            throw EmployeeServiceException.NotFound("This execution has no stored file. Run the export again to download it.");

        string? url;
        try
        {
            url = await _documents.GetResumeDownloadUrlAsync(actor.OrganizationId, documentId, cancellationToken);
        }
        catch (DocumentServiceException ex)
        {
            _logger.LogWarning("Report execution download-url failed. ExecutionId={ExecutionId} Kind={Kind}", id, ex.Kind);
            throw ex.Kind == DocumentServiceErrorKind.NotFound
                ? EmployeeServiceException.NotFound("The stored file was not found.")
                : new EmployeeServiceException(StatusCodes.Status502BadGateway, "download_failed", "The file could not be retrieved. Try again later.");
        }

        return new ReportExecutionDownloadDto
        {
            ExecutionId = id,
            DocumentId = documentId,
            DownloadPath = $"/api/documents/{documentId}/download-url",
            DownloadUrl = url,
            ExpiresAt = record.ExpiresAt
        };
    }

    // -------------------------------------------------------------------------
    // Background schedules
    // -------------------------------------------------------------------------

    /// <summary>
    /// Scheduled emails are sent when not in Development, or when <c>Reports:SendScheduledEmails</c> enables it; an explicit
    /// <c>false</c> disables them everywhere. They are never sent in the Testing environment.
    /// </summary>
    internal bool EmailSendingEnabled
    {
        get
        {
            if (_env.IsEnvironment("Testing")) return false;
            var configured = _configuration.GetValue<bool?>("Reports:SendScheduledEmails");
            return configured ?? !_env.IsDevelopment();
        }
    }

    public async Task<int> ProcessDueSchedulesAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var due = await _repo.ListDueSchedulesAsync(now, 25, cancellationToken);
        var processed = 0;

        foreach (var schedule in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var next = ReportScheduleCalculator.ComputeNextRun(schedule.Frequency, schedule.ScheduleJson, schedule.Timezone, now);

                // Claiming moves next_run_at forward so concurrent instances / the next poll cannot pick it up again.
                if (!await _repo.ClaimScheduleAsync(schedule.OrganizationId, schedule.Id, now, next, cancellationToken)) continue;

                if (await _repo.HasRecentExecutionAsync(schedule.OrganizationId, schedule.Id, now - ScheduleDedupeWindow, cancellationToken))
                {
                    await _repo.MarkScheduleRunAsync(schedule.OrganizationId, schedule.Id, now, "Skipped", next, cancellationToken);
                    continue;
                }

                processed++;
                await RunScheduleAsync(schedule, now, next, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled report {ScheduleId} failed for organization {OrganizationId}.", schedule.Id, schedule.OrganizationId);
            }
        }

        return processed;
    }

    private async Task RunScheduleAsync(ReportScheduleRecord schedule, DateTimeOffset now, DateTimeOffset next, CancellationToken ct)
    {
        var orgId = schedule.OrganizationId;
        var execId = await _repo.InsertExecutionAsync(orgId,
            new ReportExecutionInsert(schedule.ReportDefinitionId, schedule.Id, schedule.OwnerUserId, "Running", "Csv", null), ct);

        var sw = Stopwatch.StartNew();
        try
        {
            // Run strictly as the schedule owner: role changes after scheduling are honored.
            var codes = await _roles.ListRoleCodesByNexaUserAsync(orgId, schedule.OwnerUserId);
            if (!ReportsPermissions.Has(codes, ReportsPermissions.Export) || !ReportsPermissions.Has(codes, ReportsPermissions.Schedule))
                throw EmployeeServiceException.Validation("The schedule owner no longer has permission to export reports.", "owner_not_permitted");

            var report = await _repo.GetReportAsync(orgId, schedule.OwnerUserId, schedule.ReportDefinitionId, ct)
                         ?? throw EmployeeServiceException.NotFound("The scheduled report no longer exists.");
            var seeAll = ReportsPermissions.Has(codes, ReportsPermissions.ManageOrganizationReports);
            if (!CanSeeReport(report, schedule.OwnerUserId, seeAll))
                throw EmployeeServiceException.Validation("The schedule owner can no longer access this report.", "owner_not_permitted");

            var (ds, config) = ResolveConfiguration(codes, report.DatasetKey, report.ConfigurationJson);
            var outcome = await GenerateExportAsync(orgId, execId, report.Name, ds, config,
                new ReportQueryContext(orgId, Today(), MaxRowsOverride: ExportMaxRows), sw, ct);

            await SendScheduleEmailsAsync(schedule, report.Name, outcome, now, ct);
            await _repo.MarkScheduleRunAsync(orgId, schedule.Id, now, "Succeeded", next, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Scheduled report {ScheduleId} run failed.", schedule.Id);
            await TryMarkFailedAsync(orgId, execId, sw, ex, ct);
            await _repo.MarkScheduleRunAsync(orgId, schedule.Id, now, "Failed", next, CancellationToken.None);
        }
    }

    /// <summary>
    /// Emails recipients a summary and where to download it. The CSV (potentially confidential) is never attached or
    /// inlined: recipients must sign in to NextHire. Failures are logged and never fail the run.
    /// </summary>
    private async Task SendScheduleEmailsAsync(
        ReportScheduleRecord schedule, string reportName, ReportExportOutcome outcome, DateTimeOffset now, CancellationToken ct)
    {
        if (!EmailSendingEnabled)
        {
            _logger.LogInformation("Scheduled report {ScheduleId} generated; email sending is disabled in this environment.", schedule.Id);
            return;
        }

        var recipients = ParseRecipients(schedule.RecipientsJson, out var error);
        if (error != null || recipients.Count == 0) return;

        var baseUrl = (_configuration["Frontend:BaseUrl"] ?? "").TrimEnd('/');
        var reportsUrl = string.IsNullOrWhiteSpace(baseUrl) ? "" : $"{baseUrl}/hr/reports";

        var subject = $"Scheduled report ready: {reportName}";
        var text =
            $"Your scheduled report \"{reportName}\" ran on {now:yyyy-MM-dd HH:mm} UTC and contains {outcome.Result.RowCount} row(s).\n\n" +
            "For security, report data is not attached to this email. Sign in to NextHire and open Reports to download it" +
            (reportsUrl.Length > 0 ? $": {reportsUrl}" : ".") +
            "\n\nYou are receiving this because you were added as a recipient of a scheduled report.";
        var html =
            $"<p>Your scheduled report <strong>{WebUtility.HtmlEncode(reportName)}</strong> ran on {now:yyyy-MM-dd HH:mm} UTC " +
            $"and contains {outcome.Result.RowCount} row(s).</p>" +
            "<p>For security, report data is not attached to this email. Sign in to NextHire and open Reports to download it" +
            (reportsUrl.Length > 0 ? $": <a href=\"{WebUtility.HtmlEncode(reportsUrl)}\">{WebUtility.HtmlEncode(reportsUrl)}</a>" : ".") +
            "</p><p style=\"color:#6b7280;font-size:12px\">You are receiving this because you were added as a recipient of a scheduled report.</p>";

        foreach (var recipient in recipients)
        {
            try
            {
                await _email.SendEmailAsync(recipient, subject, text, html, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to email scheduled report {ScheduleId} to a recipient.", schedule.Id);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static bool IsValidJson(string value)
    {
        if (value.Length > MaxJsonChars) return false;
        try
        {
            using var _ = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
