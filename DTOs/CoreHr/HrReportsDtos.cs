namespace nexthire_api.DTOs.CoreHr;

// ---------------------------------------------------------------------------
// Filters / shared
// ---------------------------------------------------------------------------

public sealed class HrReportsFilterQuery
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string? Comparison { get; set; } // PreviousPeriod | PreviousMonth | PreviousQuarter | PreviousYear | None
    public Guid? DepartmentId { get; set; }
    public Guid? ManagerId { get; set; }
    public Guid? WorkLocationId { get; set; }
    public Guid? EmploymentTypeId { get; set; }
    public string? EmploymentStatus { get; set; }
    public Guid? OrganizationalLevelId { get; set; }
}

public sealed class MetricValueDto
{
    public double Value { get; set; }
    public double? PreviousValue { get; set; }
    public double? Delta { get; set; }
    public double? DeltaPct { get; set; }
    public string? Unit { get; set; }
}

public sealed class NamedCountDto
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public double Value { get; set; }
    public double? Percent { get; set; }
}

public sealed class TimeSeriesPointDto
{
    public string Label { get; set; } = "";
    public DateTime PeriodStart { get; set; }
    public double Value { get; set; }
}

public sealed class InsightDto
{
    public string Code { get; set; } = "";
    public string Severity { get; set; } = "Info"; // Info | Positive | Warning
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
}

// ---------------------------------------------------------------------------
// Overview analytics (screenshot)
// ---------------------------------------------------------------------------

public sealed class HrReportsOverviewDto
{
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public string ComparisonMode { get; set; } = "PreviousPeriod";
    public MetricValueDto TotalEmployees { get; set; } = new();
    public MetricValueDto NewHires { get; set; } = new();
    public MetricValueDto TurnoverRate { get; set; } = new();
    public MetricValueDto AverageTenureYears { get; set; } = new();
    public IReadOnlyList<TimeSeriesPointDto> HeadcountEvolution { get; set; } = [];
    public IReadOnlyList<NamedCountDto> WorkforceByDepartment { get; set; } = [];
    public OnboardingPerfDto Onboarding { get; set; } = new();
    public OffboardingPerfDto Offboarding { get; set; } = new();
    public DocumentsComplianceDto Documents { get; set; } = new();
    public IReadOnlyList<InsightDto> Insights { get; set; } = [];
    public IReadOnlyList<QuickReportDto> QuickReports { get; set; } = [];
}

public sealed class OnboardingPerfDto
{
    public double CompletionPct { get; set; }
    public double? PreviousCompletionPct { get; set; }
    public double AverageCompletionDays { get; set; }
    public double? PreviousAverageCompletionDays { get; set; }
    public double CompletionDaysDeltaPct { get; set; }
    public int Active { get; set; }
    public int CompletedInPeriod { get; set; }
}

public sealed class OffboardingPerfDto
{
    public double CompletionPct { get; set; }
    public double? PreviousCompletionPct { get; set; }
    public double AverageCompletionDays { get; set; }
    public double? PreviousAverageCompletionDays { get; set; }
    public double CompletionDaysDeltaPct { get; set; }
    public int Active { get; set; }
    public int CompletedInPeriod { get; set; }
}

public sealed class DocumentsComplianceDto
{
    public double CompletenessPct { get; set; }
    public int TotalDocuments { get; set; }
    public int ValidDocuments { get; set; }
    public int Expired { get; set; }
    public int ExpiringIn30Days { get; set; }
    public int OpenRequests { get; set; }
}

public sealed class QuickReportDto
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Path { get; set; } = "";
}

// ---------------------------------------------------------------------------
// Section analytics
// ---------------------------------------------------------------------------

public sealed class HrWorkforceAnalyticsDto
{
    public MetricValueDto TotalEmployees { get; set; } = new();
    public MetricValueDto ActiveEmployees { get; set; } = new();
    public MetricValueDto OnLeave { get; set; } = new();
    public MetricValueDto NewHires { get; set; } = new();
    public MetricValueDto Departures { get; set; } = new();
    public MetricValueDto NetGrowth { get; set; } = new();
    public MetricValueDto AverageTenureYears { get; set; } = new();
    public MetricValueDto RetentionRate { get; set; } = new();
    public MetricValueDto TurnoverRate { get; set; } = new();
    public IReadOnlyList<TimeSeriesPointDto> HeadcountEvolution { get; set; } = [];
    public IReadOnlyList<TimeSeriesPointDto> ArrivalsVsDeparturesArrivals { get; set; } = [];
    public IReadOnlyList<TimeSeriesPointDto> ArrivalsVsDeparturesDepartures { get; set; } = [];
    public IReadOnlyList<NamedCountDto> ByDepartment { get; set; } = [];
    public IReadOnlyList<NamedCountDto> ByLocation { get; set; } = [];
    public IReadOnlyList<NamedCountDto> ByEmploymentType { get; set; } = [];
    public IReadOnlyList<NamedCountDto> ByStatus { get; set; } = [];
    public IReadOnlyList<NamedCountDto> ByWorkModality { get; set; } = [];
    public IReadOnlyList<NamedCountDto> TenureBuckets { get; set; } = [];
}

public sealed class HrRetentionAnalyticsDto
{
    public MetricValueDto TurnoverRate { get; set; } = new();
    public MetricValueDto RetentionRate { get; set; } = new();
    public MetricValueDto AverageTenureAtDeparture { get; set; } = new();
    public int DeparturesInPeriod { get; set; }
    public IReadOnlyList<TimeSeriesPointDto> MonthlyTurnover { get; set; } = [];
    public IReadOnlyList<NamedCountDto> DeparturesByDepartment { get; set; } = [];
    public IReadOnlyList<NamedCountDto> DeparturesByEmploymentType { get; set; } = [];
    public IReadOnlyList<NamedCountDto> SeparationReasons { get; set; } = [];
    public IReadOnlyList<NamedCountDto> RetentionMilestones { get; set; } = [];
    public bool SeparationReasonsRestricted { get; set; }
}

public sealed class HrOnboardingAnalyticsDto
{
    public OnboardingPerfDto Performance { get; set; } = new();
    public int OverdueTasks { get; set; }
    public int OverdueOnboardings { get; set; }
    public double AverageProgressPercent { get; set; }
    public IReadOnlyList<NamedCountDto> ByStatus { get; set; } = [];
    public IReadOnlyList<NamedCountDto> ByDepartment { get; set; } = [];
    public IReadOnlyList<TimeSeriesPointDto> CompletionTrend { get; set; } = [];
}

public sealed class HrOffboardingAnalyticsDto
{
    public OffboardingPerfDto Performance { get; set; } = new();
    public int PendingAssetReturns { get; set; }
    public int PendingAccessRevocations { get; set; }
    public int PendingClearance { get; set; }
    public IReadOnlyList<NamedCountDto> ByStatus { get; set; } = [];
    public IReadOnlyList<NamedCountDto> BySeparationType { get; set; } = [];
    public IReadOnlyList<NamedCountDto> ByDepartment { get; set; } = [];
    public IReadOnlyList<TimeSeriesPointDto> CompletionTrend { get; set; } = [];
}

public sealed class HrOrganizationAnalyticsDto
{
    public int TotalDepartments { get; set; }
    public int TotalTeams { get; set; }
    public int TotalPositions { get; set; }
    public int ApprovedHeadcount { get; set; }
    public int ActualHeadcount { get; set; }
    public int OccupiedPositions { get; set; }
    public int VacantPositions { get; set; }
    public double HeadcountUtilizationPct { get; set; }
    public double AverageSpanOfControl { get; set; }
    public int OrganizationalDepth { get; set; }
    public int EmployeesWithoutManager { get; set; }
    public int EmployeesWithoutPosition { get; set; }
    public IReadOnlyList<NamedCountDto> UtilizationByDepartment { get; set; } = [];
    public IReadOnlyList<NamedCountDto> VacanciesByDepartment { get; set; } = [];
}

public sealed class HrDocumentsAnalyticsDto
{
    public DocumentsComplianceDto Compliance { get; set; } = new();
    public IReadOnlyList<NamedCountDto> ByCategory { get; set; } = [];
    public IReadOnlyList<NamedCountDto> CompletenessByDepartment { get; set; } = [];
    public int ExpiringIn7Days { get; set; }
    public int ExpiringIn60Days { get; set; }
}

public sealed class HrCompensationAnalyticsDto
{
    public bool Available { get; set; }
    public string? UnavailableReason { get; set; }
    public int EmployeeCount { get; set; }
    public double? AverageAnnualized { get; set; }
    public double? MedianAnnualized { get; set; }
    public string? Currency { get; set; }
    public IReadOnlyList<NamedCountDto> ByDepartment { get; set; } = [];
    public IReadOnlyList<NamedCountDto> ByJobTitle { get; set; } = [];
    public bool RestrictedByCohortSize { get; set; }
}

// ---------------------------------------------------------------------------
// Dataset registry / report execution
// ---------------------------------------------------------------------------

public sealed class ReportDatasetDto
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string RequiredPermission { get; set; } = "";
    public IReadOnlyList<ReportFieldDto> Fields { get; set; } = [];
}

public sealed class ReportFieldDto
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string DataType { get; set; } = ""; // string | number | date | boolean
    public bool IsDimension { get; set; }
    public bool IsMeasure { get; set; }
    public bool Filterable { get; set; }
    public string? RequiredPermission { get; set; }
}

public sealed class ReportDefinitionDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Category { get; set; } = "";
    public string DatasetKey { get; set; } = "";
    public string ConfigurationJson { get; set; } = "{}";
    public string? VisualizationJson { get; set; }
    public string Visibility { get; set; } = "Private";
    public bool IsSystem { get; set; }
    public bool IsFavorite { get; set; }
    public DateTimeOffset? LastViewedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? OwnerUserId { get; set; }
}

public class CreateReportDefinitionRequest
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Category { get; set; } = "Custom";
    public string DatasetKey { get; set; } = "";
    public string ConfigurationJson { get; set; } = "{}";
    public string? VisualizationJson { get; set; }
    public string Visibility { get; set; } = "Private";
}

public class UpdateReportDefinitionRequest : CreateReportDefinitionRequest { }

public sealed class ReportPreviewRequest
{
    public string DatasetKey { get; set; } = "";
    public string ConfigurationJson { get; set; } = "{}";
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public Guid? DepartmentId { get; set; }
    public int? Limit { get; set; }
}

public sealed class ReportExecutionResultDto
{
    public Guid? ExecutionId { get; set; }
    public string DatasetKey { get; set; } = "";
    public IReadOnlyList<string> Columns { get; set; } = [];
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; set; } = [];
    public int RowCount { get; set; }
    public int TruncatedAt { get; set; }
    public IReadOnlyList<NamedCountDto>? Series { get; set; }
}

public sealed class ReportExportRequest
{
    public string Format { get; set; } = "Csv"; // Csv | Excel
    public string? ConfigurationJson { get; set; }
}

public sealed class ReportExportResultDto
{
    public Guid ExecutionId { get; set; }
    public string Format { get; set; } = "Csv";
    public Guid? DocumentId { get; set; }
    public string? DownloadPath { get; set; }
    public int RowCount { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

// ---------------------------------------------------------------------------
// Dashboards / schedules
// ---------------------------------------------------------------------------

public sealed class ReportDashboardDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Visibility { get; set; } = "Private";
    public bool IsDefault { get; set; }
    public string? GlobalFiltersJson { get; set; }
    public string? LayoutJson { get; set; }
    public IReadOnlyList<ReportDashboardWidgetDto> Widgets { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ReportDashboardWidgetDto
{
    public Guid Id { get; set; }
    public Guid? ReportDefinitionId { get; set; }
    public string WidgetType { get; set; } = "Chart";
    public string? Title { get; set; }
    public string ConfigurationJson { get; set; } = "{}";
    public int GridX { get; set; }
    public int GridY { get; set; }
    public int GridW { get; set; } = 4;
    public int GridH { get; set; } = 3;
    public int SortOrder { get; set; }
}

public sealed class UpsertReportDashboardRequest
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Visibility { get; set; } = "Private";
    public bool IsDefault { get; set; }
    public string? GlobalFiltersJson { get; set; }
    public string? LayoutJson { get; set; }
    public IReadOnlyList<UpsertReportDashboardWidgetRequest>? Widgets { get; set; }
}

public sealed class UpsertReportDashboardWidgetRequest
{
    public Guid? Id { get; set; }
    public Guid? ReportDefinitionId { get; set; }
    public string WidgetType { get; set; } = "Chart";
    public string? Title { get; set; }
    public string ConfigurationJson { get; set; } = "{}";
    public int GridX { get; set; }
    public int GridY { get; set; }
    public int GridW { get; set; } = 4;
    public int GridH { get; set; } = 3;
    public int SortOrder { get; set; }
}

public sealed class ReportScheduleDto
{
    public Guid Id { get; set; }
    public Guid ReportDefinitionId { get; set; }
    public string ReportName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Frequency { get; set; } = "Weekly";
    public string Timezone { get; set; } = "UTC";
    public string OutputFormat { get; set; } = "Csv";
    public string RecipientsJson { get; set; } = "[]";
    public bool IsActive { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public string? LastStatus { get; set; }
}

public sealed class UpsertReportScheduleRequest
{
    public Guid ReportDefinitionId { get; set; }
    public string Name { get; set; } = "";
    public string Frequency { get; set; } = "Weekly";
    public string? ScheduleJson { get; set; }
    public string Timezone { get; set; } = "UTC";
    public string OutputFormat { get; set; } = "Csv";
    public string RecipientsJson { get; set; } = "[]";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? NextRunAt { get; set; }
}

public sealed class ReportExecutionDto
{
    public Guid Id { get; set; }
    public Guid? ReportDefinitionId { get; set; }
    public Guid? ScheduleId { get; set; }
    public string Status { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int? DurationMs { get; set; }
    public int? RowCount { get; set; }
    public string? OutputFormat { get; set; }
    public Guid? DocumentId { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}
