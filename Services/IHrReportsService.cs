using nexthire_api.DTOs.CoreHr;

namespace nexthire_api.Services;

/// <summary>A generated export: persisted metadata plus the bytes for an immediate download.</summary>
public sealed record ReportExportOutcome(ReportExportResultDto Result, byte[] Content, string FileName, string ContentType);

/// <summary>Download information for a stored execution output.</summary>
public sealed class ReportExecutionDownloadDto
{
    public Guid ExecutionId { get; set; }
    public Guid DocumentId { get; set; }

    /// <summary>Authenticated API path that returns the signed URL: <c>/api/documents/{documentId}/download-url</c>.</summary>
    public string DownloadPath { get; set; } = "";

    /// <summary>Signed URL resolved now (short lived); never log it.</summary>
    public string? DownloadUrl { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>
/// Core HR Reports &amp; People Analytics. Every method resolves the caller's role codes and enforces
/// <see cref="nexthire_api.Security.ReportsPermissions"/> (<see cref="nexthire_api.Exceptions.HrAccessDeniedException"/>
/// otherwise). Business-rule failures throw <see cref="nexthire_api.Exceptions.EmployeeServiceException"/>; missing (or
/// not visible) records return null / false.
/// </summary>
public interface IHrReportsService
{
    Task<IReadOnlyList<string>> GetCapabilitiesAsync(HrActor actor, CancellationToken cancellationToken = default);

    // Analytics
    Task<HrReportsOverviewDto> GetOverviewAsync(HrActor actor, HrReportsFilterQuery? filter, CancellationToken cancellationToken = default);
    Task<HrWorkforceAnalyticsDto> GetWorkforceAsync(HrActor actor, HrReportsFilterQuery? filter, CancellationToken cancellationToken = default);
    Task<HrRetentionAnalyticsDto> GetRetentionAsync(HrActor actor, HrReportsFilterQuery? filter, CancellationToken cancellationToken = default);
    Task<HrOnboardingAnalyticsDto> GetOnboardingAsync(HrActor actor, HrReportsFilterQuery? filter, CancellationToken cancellationToken = default);
    Task<HrOffboardingAnalyticsDto> GetOffboardingAsync(HrActor actor, HrReportsFilterQuery? filter, CancellationToken cancellationToken = default);
    Task<HrOrganizationAnalyticsDto> GetOrganizationAsync(HrActor actor, HrReportsFilterQuery? filter, CancellationToken cancellationToken = default);
    Task<HrDocumentsAnalyticsDto> GetDocumentsAsync(HrActor actor, HrReportsFilterQuery? filter, CancellationToken cancellationToken = default);
    Task<HrCompensationAnalyticsDto> GetCompensationAsync(HrActor actor, HrReportsFilterQuery? filter, CancellationToken cancellationToken = default);

    // Datasets (only those the caller may use; restricted fields are hidden)
    Task<IReadOnlyList<ReportDatasetDto>> ListDatasetsAsync(HrActor actor, CancellationToken cancellationToken = default);
    Task<ReportDatasetDto?> GetDatasetAsync(HrActor actor, string key, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReportFieldDto>?> GetDatasetFieldsAsync(HrActor actor, string key, CancellationToken cancellationToken = default);

    // Report definitions
    Task<IReadOnlyList<ReportDefinitionDto>> ListReportsAsync(HrActor actor, string? category, string? search, bool favoritesOnly, CancellationToken cancellationToken = default);
    Task<ReportDefinitionDto?> GetReportAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default);
    Task<ReportDefinitionDto> CreateReportAsync(HrActor actor, CreateReportDefinitionRequest request, CancellationToken cancellationToken = default);
    Task<ReportDefinitionDto?> UpdateReportAsync(HrActor actor, Guid id, UpdateReportDefinitionRequest request, CancellationToken cancellationToken = default);
    Task<ReportDefinitionDto?> DuplicateReportAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default);

    /// <summary>Soft-archives a custom report (system reports cannot be archived). Returns false when not found.</summary>
    Task<bool> ArchiveReportAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default);
    Task<bool> SetFavoriteAsync(HrActor actor, Guid id, bool isFavorite, CancellationToken cancellationToken = default);

    // Run
    Task<ReportExecutionResultDto> PreviewAsync(HrActor actor, ReportPreviewRequest request, CancellationToken cancellationToken = default);
    Task<ReportExecutionResultDto?> ExecuteAsync(HrActor actor, Guid id, ReportPreviewRequest? overrides, CancellationToken cancellationToken = default);

    /// <summary>Generates a CSV, persists execution metadata and (when possible) stores the file in Documents.</summary>
    Task<ReportExportOutcome?> ExportAsync(HrActor actor, Guid id, ReportExportRequest request, CancellationToken cancellationToken = default);

    // Dashboards
    Task<IReadOnlyList<ReportDashboardDto>> ListDashboardsAsync(HrActor actor, CancellationToken cancellationToken = default);
    Task<ReportDashboardDto?> GetDashboardAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default);
    Task<ReportDashboardDto> CreateDashboardAsync(HrActor actor, UpsertReportDashboardRequest request, CancellationToken cancellationToken = default);
    Task<ReportDashboardDto?> UpdateDashboardAsync(HrActor actor, Guid id, UpsertReportDashboardRequest request, CancellationToken cancellationToken = default);
    Task<bool> ArchiveDashboardAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default);

    // Schedules
    Task<IReadOnlyList<ReportScheduleDto>> ListSchedulesAsync(HrActor actor, CancellationToken cancellationToken = default);
    Task<ReportScheduleDto?> GetScheduleAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default);
    Task<ReportScheduleDto> CreateScheduleAsync(HrActor actor, UpsertReportScheduleRequest request, CancellationToken cancellationToken = default);
    Task<ReportScheduleDto?> UpdateScheduleAsync(HrActor actor, Guid id, UpsertReportScheduleRequest request, CancellationToken cancellationToken = default);
    Task<bool> ArchiveScheduleAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReportExecutionDto>?> GetScheduleHistoryAsync(HrActor actor, Guid id, int take, CancellationToken cancellationToken = default);

    // Executions
    Task<IReadOnlyList<ReportExecutionDto>> ListExecutionsAsync(HrActor actor, Guid? reportId, int take, CancellationToken cancellationToken = default);
    Task<ReportExecutionDto?> GetExecutionAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default);
    Task<ReportExecutionDownloadDto?> GetExecutionDownloadAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Background entry point: runs due schedules (as each schedule's owner), stores outputs and emails recipients
    /// when sending is enabled. Returns the number of schedules processed. Never throws for a single failing schedule.
    /// </summary>
    Task<int> ProcessDueSchedulesAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
}
