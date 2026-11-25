using nexthire_api.DTOs.CoreHr;
using nexthire_api.Services.Reporting;

namespace nexthire_api.Repositories;

/// <summary>Overview sections that need data beyond the workforce core.</summary>
[Flags]
public enum HrOverviewSections
{
    None = 0,
    Onboarding = 1,
    Offboarding = 2,
    Documents = 4,
    All = Onboarding | Offboarding | Documents
}

/// <summary>Writable fields of a report definition (already validated by the service).</summary>
public sealed record ReportDefinitionWrite(
    string Name,
    string? Description,
    string Category,
    string DatasetKey,
    string ConfigurationJson,
    string? VisualizationJson,
    string Visibility);

/// <summary>Seed row for a system report card.</summary>
public sealed record SystemReportSeed(
    string Key,
    string Name,
    string Description,
    string Category,
    string DatasetKey,
    string ConfigurationJson,
    string? VisualizationJson);

/// <summary>Result of an executed dataset query; rows are keyed by output column key.</summary>
public sealed record ReportQueryResult(
    IReadOnlyList<ReportQueryColumn> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    bool Truncated,
    int RowLimit);

public sealed record ReportDashboardRecord(ReportDashboardDto Dashboard, Guid OwnerUserId);

public sealed record ReportDashboardWrite(
    string Name,
    string? Description,
    string Visibility,
    bool IsDefault,
    string? GlobalFiltersJson,
    string? LayoutJson);

public sealed record ReportDashboardWidgetWrite(
    Guid? ReportDefinitionId,
    string WidgetType,
    string? Title,
    string ConfigurationJson,
    int GridX,
    int GridY,
    int GridW,
    int GridH,
    int SortOrder);

/// <summary>Schedule row including tenant and owner (the hosted service resolves permissions as the owner).</summary>
public sealed class ReportScheduleRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ReportDefinitionId { get; set; }
    public string ReportName { get; set; } = "";
    public Guid OwnerUserId { get; set; }
    public string Name { get; set; } = "";
    public string Frequency { get; set; } = "Weekly";
    public string? ScheduleJson { get; set; }
    public string Timezone { get; set; } = "UTC";
    public string OutputFormat { get; set; } = "Csv";
    public string RecipientsJson { get; set; } = "[]";
    public bool IsActive { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public string? LastStatus { get; set; }
}

public sealed record ReportScheduleWrite(
    Guid ReportDefinitionId,
    string Name,
    string Frequency,
    string? ScheduleJson,
    string Timezone,
    string OutputFormat,
    string RecipientsJson,
    bool IsActive,
    DateTimeOffset? NextRunAt);

public sealed record ReportExecutionInsert(
    Guid? ReportDefinitionId,
    Guid? ScheduleId,
    Guid? RequestedBy,
    string Status,
    string? OutputFormat,
    string? FiltersJson);

public sealed record ReportExecutionUpdate(
    string Status,
    DateTimeOffset CompletedAt,
    int DurationMs,
    int? RowCount,
    string? ErrorCode,
    string? ErrorMessage,
    Guid? DocumentId,
    DateTimeOffset? ExpiresAt);

public sealed class ReportExecutionRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? ReportDefinitionId { get; set; }
    public Guid? ScheduleId { get; set; }
    public Guid? RequestedBy { get; set; }
    public string Status { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int? DurationMs { get; set; }
    public int? RowCount { get; set; }
    public string? OutputFormat { get; set; }
    public Guid? DocumentId { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    public ReportExecutionDto ToDto() => new()
    {
        Id = Id,
        ReportDefinitionId = ReportDefinitionId,
        ScheduleId = ScheduleId,
        Status = Status,
        StartedAt = StartedAt,
        CompletedAt = CompletedAt,
        DurationMs = DurationMs,
        RowCount = RowCount,
        OutputFormat = OutputFormat,
        DocumentId = DocumentId,
        ErrorMessage = ErrorMessage,
        ExpiresAt = ExpiresAt
    };
}

/// <summary>
/// Core HR Reports &amp; People Analytics persistence (Dapper / SQL Server). Every query is tenant scoped by
/// <c>organization_id</c> (the only cross-tenant read is <see cref="ListDueSchedulesAsync"/>, used by the scheduler);
/// archives are soft. No Recruitment tables are read.
/// </summary>
public interface IHrReportsRepository
{
    Task EnsureSchemaAsync();

    /// <summary>Upserts the system report cards (is_system = 1) for the organization. Idempotent.</summary>
    Task EnsureSystemReportsAsync(Guid organizationId, Guid? actorUserId, CancellationToken cancellationToken = default);

    // Analytics

    /// <param name="sections">Optional sections to include; excluded sections stay at their defaults (permission gating by the service).</param>
    Task<HrReportsOverviewDto> GetOverviewAsync(Guid organizationId, HrReportsFilterQuery filter, DateTime today, HrOverviewSections sections = HrOverviewSections.All, CancellationToken cancellationToken = default);
    Task<HrWorkforceAnalyticsDto> GetWorkforceAsync(Guid organizationId, HrReportsFilterQuery filter, DateTime today, CancellationToken cancellationToken = default);

    /// <param name="includeSeparationReasons">False masks separation reasons (confidential) and sets <c>SeparationReasonsRestricted</c>.</param>
    Task<HrRetentionAnalyticsDto> GetRetentionAsync(Guid organizationId, HrReportsFilterQuery filter, DateTime today, bool includeSeparationReasons, CancellationToken cancellationToken = default);
    Task<HrOnboardingAnalyticsDto> GetOnboardingAsync(Guid organizationId, HrReportsFilterQuery filter, DateTime today, CancellationToken cancellationToken = default);
    Task<HrOffboardingAnalyticsDto> GetOffboardingAsync(Guid organizationId, HrReportsFilterQuery filter, DateTime today, CancellationToken cancellationToken = default);
    Task<HrOrganizationAnalyticsDto> GetOrganizationAsync(Guid organizationId, HrReportsFilterQuery filter, DateTime today, CancellationToken cancellationToken = default);
    Task<HrDocumentsAnalyticsDto> GetDocumentsAsync(Guid organizationId, HrReportsFilterQuery filter, DateTime today, CancellationToken cancellationToken = default);

    /// <summary>Annualized estimate; <c>Available=false</c> when there is no data, mixed currencies or fewer than 5 employees.</summary>
    Task<HrCompensationAnalyticsDto> GetCompensationAsync(Guid organizationId, HrReportsFilterQuery filter, DateTime today, CancellationToken cancellationToken = default);

    /// <summary>Monthly headcount points from <c>From</c> to <c>To</c> (hire_date / termination_date logic).</summary>
    Task<IReadOnlyList<TimeSeriesPointDto>> GetHeadcountEvolutionAsync(Guid organizationId, HrReportsFilterQuery filter, DateTime today, CancellationToken cancellationToken = default);

    // Report definitions
    Task<IReadOnlyList<ReportDefinitionDto>> ListReportsAsync(Guid organizationId, Guid userId, bool seeAll, string? category, string? search, bool favoritesOnly, CancellationToken cancellationToken = default);
    Task<ReportDefinitionDto?> GetReportAsync(Guid organizationId, Guid userId, Guid id, CancellationToken cancellationToken = default);
    Task<Guid> InsertReportAsync(Guid organizationId, Guid userId, ReportDefinitionWrite row, CancellationToken cancellationToken = default);

    /// <summary>Updates a non-system, non-archived definition. Returns false when not found.</summary>
    Task<bool> UpdateReportAsync(Guid organizationId, Guid userId, Guid id, ReportDefinitionWrite row, CancellationToken cancellationToken = default);

    /// <summary>Soft-archives a non-system definition and deactivates its schedules.</summary>
    Task<bool> ArchiveReportAsync(Guid organizationId, Guid userId, Guid id, CancellationToken cancellationToken = default);
    Task<Guid?> DuplicateReportAsync(Guid organizationId, Guid userId, Guid id, string newName, CancellationToken cancellationToken = default);
    Task SetFavoriteAsync(Guid organizationId, Guid userId, Guid reportId, bool isFavorite, CancellationToken cancellationToken = default);
    Task RecordViewAsync(Guid organizationId, Guid userId, Guid reportId, CancellationToken cancellationToken = default);

    /// <summary>Runs a query produced by <see cref="ReportQueryBuilder"/> (already tenant scoped) and maps rows to output keys.</summary>
    Task<ReportQueryResult> ExecuteReportQueryAsync(BuiltReportQuery query, CancellationToken cancellationToken = default);

    // Dashboards
    Task<IReadOnlyList<ReportDashboardDto>> ListDashboardsAsync(Guid organizationId, Guid userId, bool seeAll, CancellationToken cancellationToken = default);
    Task<ReportDashboardRecord?> GetDashboardAsync(Guid organizationId, Guid id, CancellationToken cancellationToken = default);
    Task<Guid> InsertDashboardAsync(Guid organizationId, Guid ownerUserId, ReportDashboardWrite row, IReadOnlyList<ReportDashboardWidgetWrite>? widgets, CancellationToken cancellationToken = default);

    /// <summary>Updates the dashboard; when <paramref name="widgets"/> is not null the widget set is replaced.</summary>
    Task<bool> UpdateDashboardAsync(Guid organizationId, Guid userId, Guid id, ReportDashboardWrite row, IReadOnlyList<ReportDashboardWidgetWrite>? widgets, CancellationToken cancellationToken = default);
    Task<bool> ArchiveDashboardAsync(Guid organizationId, Guid userId, Guid id, CancellationToken cancellationToken = default);

    // Schedules
    Task<IReadOnlyList<ReportScheduleRecord>> ListSchedulesAsync(Guid organizationId, Guid userId, bool seeAll, CancellationToken cancellationToken = default);
    Task<ReportScheduleRecord?> GetScheduleAsync(Guid organizationId, Guid id, CancellationToken cancellationToken = default);
    Task<Guid> InsertScheduleAsync(Guid organizationId, Guid ownerUserId, ReportScheduleWrite row, CancellationToken cancellationToken = default);
    Task<bool> UpdateScheduleAsync(Guid organizationId, Guid userId, Guid id, ReportScheduleWrite row, CancellationToken cancellationToken = default);
    Task<bool> ArchiveScheduleAsync(Guid organizationId, Guid userId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>Active, non-archived schedules whose next_run_at is due. Spans tenants (scheduler only).</summary>
    Task<IReadOnlyList<ReportScheduleRecord>> ListDueSchedulesAsync(DateTimeOffset now, int take, CancellationToken cancellationToken = default);

    /// <summary>Atomically moves next_run_at forward so only one instance processes a due schedule. False when already claimed.</summary>
    Task<bool> ClaimScheduleAsync(Guid organizationId, Guid id, DateTimeOffset now, DateTimeOffset nextRunAt, CancellationToken cancellationToken = default);
    Task MarkScheduleRunAsync(Guid organizationId, Guid id, DateTimeOffset lastRunAt, string status, DateTimeOffset? nextRunAt, CancellationToken cancellationToken = default);

    // Executions
    Task<Guid> InsertExecutionAsync(Guid organizationId, ReportExecutionInsert row, CancellationToken cancellationToken = default);
    Task UpdateExecutionAsync(Guid organizationId, Guid id, ReportExecutionUpdate update, CancellationToken cancellationToken = default);
    Task<ReportExecutionRecord?> GetExecutionAsync(Guid organizationId, Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReportExecutionRecord>> ListExecutionsByScheduleAsync(Guid organizationId, Guid scheduleId, int take, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReportExecutionRecord>> ListExecutionsAsync(Guid organizationId, Guid? requestedBy, Guid? reportDefinitionId, int take, CancellationToken cancellationToken = default);
    Task<bool> HasRecentExecutionAsync(Guid organizationId, Guid scheduleId, DateTimeOffset since, CancellationToken cancellationToken = default);
}
