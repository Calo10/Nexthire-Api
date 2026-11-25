using System.Data;
using System.Globalization;
using System.Text;
using Dapper;
using nexthire_api.DTOs.CoreHr;
using nexthire_api.Services.Reporting;

namespace nexthire_api.Repositories;

// People analytics: workforce, retention, onboarding, offboarding, organization, documents, compensation.
// Workforce metrics are computed from the employee rows (hire_date / termination_date logic mirrors
// EmployeeRepository.HeadcountAtAsync) so every series uses one consistent definition of "headcount".
public partial class HrReportsRepository
{
    public const int MinimumCohortSize = 5;
    private const int MaxMonthPoints = 120;
    private const string ActiveOnboardingStatuses = "N'NotStarted', N'InProgress', N'InReview'";
    private const string ActiveOffboardingStatuses = "N'Scheduled', N'InProgress', N'PendingClearance'";
    private const string OpenTaskStatuses = "N'Pending', N'InProgress', N'Blocked'";

    // -------------------------------------------------------------------------
    // Shared types / helpers
    // -------------------------------------------------------------------------

    private sealed class EmpRow
    {
        public Guid Id { get; set; }
        public Guid? DepartmentId { get; set; }
        public string? DepartmentName { get; set; }
        public Guid? ManagerId { get; set; }
        public string? LocationName { get; set; }
        public string? EmploymentTypeName { get; set; }
        public string Status { get; set; } = "";
        public string? Modality { get; set; }

        /// <summary>hire_date, or the creation date when no hire date was recorded (headcount logic).</summary>
        public DateTime EffectiveHire { get; set; }

        /// <summary>Recorded hire_date only (new hires, tenure).</summary>
        public DateTime? HireDate { get; set; }

        public DateTime? TerminationDate { get; set; }
        public bool HasPosition { get; set; }

        public string DepartmentLabel => string.IsNullOrWhiteSpace(DepartmentName) ? "Unassigned" : DepartmentName!;
    }

    private sealed record Period(DateTime Start, DateTime End, DateTime? PrevStart, DateTime? PrevEnd, string Mode);

    private sealed class KeyCount
    {
        public string Key { get; set; } = "";
        public int Cnt { get; set; }
        public int Valid { get; set; }
    }

    private sealed class PerfAgg
    {
        public int Done { get; set; }
        public int Total { get; set; }
    }

    private sealed class CompletedAgg
    {
        public int Completed { get; set; }
        public double? AvgDays { get; set; }
    }

    private static DynamicParameters NewParams(Guid organizationId, DateTime today)
    {
        var p = new DynamicParameters();
        p.Add("OrganizationId", organizationId);
        p.Add("Today", today.Date);
        return p;
    }

    /// <summary>Resolves From/To (default: trailing 12 months) and the comparison window.</summary>
    private static Period ResolvePeriod(HrReportsFilterQuery filter, DateTime today)
    {
        var end = (filter.To ?? today).Date;
        var start = (filter.From ?? end.AddMonths(-12).AddDays(1)).Date;
        if (start > end) (start, end) = (end, start);
        if (start < end.AddMonths(-MaxMonthPoints + 1)) start = end.AddMonths(-MaxMonthPoints + 1);

        var requested = filter.Comparison?.Trim();
        var mode = requested switch
        {
            null or "" => "PreviousPeriod",
            _ when requested.Equals("None", StringComparison.OrdinalIgnoreCase) => "None",
            _ when requested.Equals("PreviousMonth", StringComparison.OrdinalIgnoreCase) => "PreviousMonth",
            _ when requested.Equals("PreviousQuarter", StringComparison.OrdinalIgnoreCase) => "PreviousQuarter",
            _ when requested.Equals("PreviousYear", StringComparison.OrdinalIgnoreCase) => "PreviousYear",
            _ => "PreviousPeriod"
        };

        DateTime? prevStart = null, prevEnd = null;
        switch (mode)
        {
            case "PreviousPeriod":
                var days = (end - start).Days + 1;
                prevEnd = start.AddDays(-1);
                prevStart = prevEnd.Value.AddDays(-(days - 1));
                break;
            case "PreviousMonth":
                prevStart = start.AddMonths(-1);
                prevEnd = end.AddMonths(-1);
                break;
            case "PreviousQuarter":
                prevStart = start.AddMonths(-3);
                prevEnd = end.AddMonths(-3);
                break;
            case "PreviousYear":
                prevStart = start.AddYears(-1);
                prevEnd = end.AddYears(-1);
                break;
        }

        return new Period(start, end, prevStart, prevEnd, mode);
    }

    /// <summary>Employee scope shared by every analytics query. Adds its parameters to <paramref name="p"/>.</summary>
    private static string EmpFilterSql(HrReportsFilterQuery f, string alias, DynamicParameters p)
    {
        var sb = new StringBuilder();
        if (f.DepartmentId.HasValue)
        {
            sb.Append($" AND {alias}.department_id = @FDepartmentId");
            p.Add("FDepartmentId", f.DepartmentId.Value);
        }
        if (f.ManagerId.HasValue)
        {
            sb.Append($" AND {alias}.manager_id = @FManagerId");
            p.Add("FManagerId", f.ManagerId.Value);
        }
        if (f.WorkLocationId.HasValue)
        {
            sb.Append($" AND {alias}.work_location_id = @FLocationId");
            p.Add("FLocationId", f.WorkLocationId.Value);
        }
        if (f.EmploymentTypeId.HasValue)
        {
            sb.Append($" AND {alias}.employment_type_id = @FEmploymentTypeId");
            p.Add("FEmploymentTypeId", f.EmploymentTypeId.Value);
        }
        if (!string.IsNullOrWhiteSpace(f.EmploymentStatus))
        {
            sb.Append($" AND {alias}.employment_status = @FStatus");
            p.Add("FStatus", f.EmploymentStatus.Trim());
        }
        if (f.OrganizationalLevelId.HasValue)
        {
            sb.Append($@" AND EXISTS (SELECT 1 FROM dbo.nh_hr_employee_position_assignments fa
   JOIN dbo.nh_hr_organizational_positions fp ON fp.id = fa.position_id AND fp.organization_id = fa.organization_id
   WHERE fa.organization_id = {alias}.organization_id AND fa.employee_id = {alias}.id
     AND fa.end_date IS NULL AND fa.status = N'Active' AND fp.organizational_level_id = @FLevelId)");
            p.Add("FLevelId", f.OrganizationalLevelId.Value);
        }
        return sb.ToString();
    }

    private async Task<List<EmpRow>> LoadEmployeesAsync(
        IDbConnection connection, Guid organizationId, HrReportsFilterQuery filter, DateTime today, CancellationToken ct)
    {
        var p = NewParams(organizationId, today);
        var emp = EmpFilterSql(filter, "e", p);
        var sql = $@"
SELECT e.id AS Id, e.department_id AS DepartmentId, d.name AS DepartmentName, e.manager_id AS ManagerId,
       wl.name AS LocationName, et.name AS EmploymentTypeName, e.employment_status AS Status, e.work_modality AS Modality,
       COALESCE(e.hire_date, CAST(e.created_at AS date)) AS EffectiveHire, e.hire_date AS HireDate,
       e.termination_date AS TerminationDate,
       CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.nh_hr_employee_position_assignments a
                              WHERE a.organization_id = e.organization_id AND a.employee_id = e.id
                                AND a.end_date IS NULL AND a.status = N'Active') THEN 1 ELSE 0 END AS bit) AS HasPosition
FROM dbo.nh_hr_employees e
LEFT JOIN dbo.nh_hr_departments d ON d.id = e.department_id AND d.organization_id = e.organization_id
LEFT JOIN dbo.nh_hr_work_locations wl ON wl.id = e.work_location_id AND wl.organization_id = e.organization_id
LEFT JOIN dbo.nh_hr_employment_types et ON et.id = e.employment_type_id AND et.organization_id = e.organization_id
WHERE e.organization_id = @OrganizationId AND e.archived_at IS NULL{emp};";
        return (await connection.QueryAsync<EmpRow>(new CommandDefinition(sql, p, cancellationToken: ct))).ToList();
    }

    // ---- in-memory workforce primitives -------------------------------------

    private static bool InHeadcount(EmpRow r, DateTime asOf) =>
        r.EffectiveHire.Date <= asOf.Date && (r.TerminationDate == null || r.TerminationDate.Value.Date > asOf.Date);

    private static int HeadcountAt(IEnumerable<EmpRow> rows, DateTime asOf) => rows.Count(r => InHeadcount(r, asOf));

    private static int HiresBetween(IEnumerable<EmpRow> rows, DateTime start, DateTime end) =>
        rows.Count(r => r.HireDate.HasValue && r.HireDate.Value.Date >= start.Date && r.HireDate.Value.Date <= end.Date);

    private static List<EmpRow> DeparturesBetween(IEnumerable<EmpRow> rows, DateTime start, DateTime end) =>
        rows.Where(r => r.TerminationDate.HasValue && r.Status == "Terminated"
                        && r.TerminationDate.Value.Date >= start.Date && r.TerminationDate.Value.Date <= end.Date).ToList();

    private static double TurnoverRate(IReadOnlyList<EmpRow> rows, DateTime start, DateTime end)
    {
        var departures = DeparturesBetween(rows, start, end).Count;
        var average = (HeadcountAt(rows, start.AddDays(-1)) + HeadcountAt(rows, end)) / 2.0;
        return average <= 0 ? 0 : Math.Round(departures / average * 100.0, 2);
    }

    private static double RetentionRate(IReadOnlyList<EmpRow> rows, DateTime start, DateTime end)
    {
        var startSet = rows.Where(r => InHeadcount(r, start.AddDays(-1))).ToList();
        if (startSet.Count == 0) return 0;
        var retained = startSet.Count(r => r.TerminationDate == null || r.TerminationDate.Value.Date > end.Date);
        return Math.Round(retained * 100.0 / startSet.Count, 2);
    }

    private static double AverageTenureYears(IReadOnlyList<EmpRow> rows, DateTime asOf)
    {
        var tenures = rows.Where(r => InHeadcount(r, asOf) && r.HireDate.HasValue && r.HireDate.Value.Date <= asOf.Date)
            .Select(r => (asOf.Date - r.HireDate!.Value.Date).TotalDays / 365.25).ToList();
        return tenures.Count == 0 ? 0 : Math.Round(tenures.Average(), 2);
    }

    private static IEnumerable<(DateTime MonthStart, DateTime PointStart, DateTime PointEnd)> Months(DateTime start, DateTime end)
    {
        var cursor = new DateTime(start.Year, start.Month, 1);
        var guard = 0;
        while (cursor <= end && guard++ < MaxMonthPoints)
        {
            var monthEnd = cursor.AddMonths(1).AddDays(-1);
            yield return (cursor, cursor < start ? start : cursor, monthEnd > end ? end : monthEnd);
            cursor = cursor.AddMonths(1);
        }
    }

    private static string MonthLabel(DateTime monthStart) => monthStart.ToString("MMM yyyy", CultureInfo.InvariantCulture);

    private static List<TimeSeriesPointDto> HeadcountSeries(IReadOnlyList<EmpRow> rows, Period period) =>
        Months(period.Start, period.End)
            .Select(m => new TimeSeriesPointDto { Label = MonthLabel(m.MonthStart), PeriodStart = m.MonthStart, Value = HeadcountAt(rows, m.PointEnd) })
            .ToList();

    private static MetricValueDto Metric(double value, double? previous, string? unit = null, int digits = 2)
    {
        var metric = new MetricValueDto { Value = Math.Round(value, digits), Unit = unit };
        if (previous.HasValue)
        {
            var prev = Math.Round(previous.Value, digits);
            metric.PreviousValue = prev;
            metric.Delta = Math.Round(metric.Value - prev, digits);
            if (prev > 0) metric.DeltaPct = Math.Round((metric.Value - prev) / prev * 100.0, 1);
        }
        return metric;
    }

    private static List<NamedCountDto> GroupNamed<T>(IEnumerable<T> items, Func<T, string> label, int? take = null, string? unassignedKey = null)
    {
        var list = items.ToList();
        var total = list.Count;
        var grouped = list.GroupBy(label, StringComparer.OrdinalIgnoreCase)
            .Select(g => new NamedCountDto
            {
                Key = g.Key,
                Label = g.Key,
                Value = g.Count(),
                Percent = total == 0 ? 0 : Math.Round(g.Count() * 100.0 / total, 1)
            })
            .OrderByDescending(x => x.Value).ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase);
        return (take.HasValue ? grouped.Take(take.Value) : grouped).ToList();
    }

    private static List<NamedCountDto> ToNamed(IEnumerable<KeyCount> rows)
    {
        var list = rows.ToList();
        var total = list.Sum(r => r.Cnt);
        return list.OrderByDescending(r => r.Cnt).ThenBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
            .Select(r => new NamedCountDto
            {
                Key = r.Key,
                Label = r.Key,
                Value = r.Cnt,
                Percent = total == 0 ? 0 : Math.Round(r.Cnt * 100.0 / total, 1)
            }).ToList();
    }

    private static List<TimeSeriesPointDto> MonthlyCounts(IEnumerable<DateTime> dates, Period period)
    {
        var list = dates.Select(d => d.Date).ToList();
        return Months(period.Start, period.End)
            .Select(m => new TimeSeriesPointDto
            {
                Label = MonthLabel(m.MonthStart),
                PeriodStart = m.MonthStart,
                Value = list.Count(d => d >= m.PointStart && d <= m.PointEnd)
            }).ToList();
    }

    // -------------------------------------------------------------------------
    // Overview
    // -------------------------------------------------------------------------

    public async Task<HrReportsOverviewDto> GetOverviewAsync(
        Guid organizationId, HrReportsFilterQuery filter, DateTime today, HrOverviewSections sections = HrOverviewSections.All,
        CancellationToken cancellationToken = default)
    {
        var period = ResolvePeriod(filter, today);
        using var connection = OpenConnection();
        var rows = await LoadEmployeesAsync(connection, organizationId, filter, today, cancellationToken);

        var hasPrev = period.PrevEnd.HasValue;
        var headcountEnd = HeadcountAt(rows, period.End);
        var current = rows.Where(r => InHeadcount(r, period.End)).ToList();

        var overview = new HrReportsOverviewDto
        {
            PeriodStart = period.Start,
            PeriodEnd = period.End,
            ComparisonMode = period.Mode,
            TotalEmployees = Metric(headcountEnd, hasPrev ? HeadcountAt(rows, period.PrevEnd!.Value) : null, "employees", 0),
            NewHires = Metric(HiresBetween(rows, period.Start, period.End),
                hasPrev ? HiresBetween(rows, period.PrevStart!.Value, period.PrevEnd!.Value) : null, "employees", 0),
            TurnoverRate = Metric(TurnoverRate(rows, period.Start, period.End),
                hasPrev ? TurnoverRate(rows, period.PrevStart!.Value, period.PrevEnd!.Value) : null, "%"),
            AverageTenureYears = Metric(AverageTenureYears(rows, period.End),
                hasPrev ? AverageTenureYears(rows, period.PrevEnd!.Value) : null, "years"),
            HeadcountEvolution = HeadcountSeries(rows, period),
            WorkforceByDepartment = GroupNamed(current, r => r.DepartmentLabel)
        };

        if (sections.HasFlag(HrOverviewSections.Onboarding))
            overview.Onboarding = await OnboardingPerfAsync(connection, organizationId, period, filter, today, cancellationToken);
        if (sections.HasFlag(HrOverviewSections.Offboarding))
            overview.Offboarding = await OffboardingPerfAsync(connection, organizationId, period, filter, today, cancellationToken);
        if (sections.HasFlag(HrOverviewSections.Documents))
            overview.Documents = (await DocumentsAggregateAsync(connection, organizationId, filter, today, cancellationToken)).Compliance;

        overview.Insights = HrReportInsights.Build(overview);
        return overview;
    }

    public async Task<IReadOnlyList<TimeSeriesPointDto>> GetHeadcountEvolutionAsync(
        Guid organizationId, HrReportsFilterQuery filter, DateTime today, CancellationToken cancellationToken = default)
    {
        var period = ResolvePeriod(filter, today);
        using var connection = OpenConnection();
        var rows = await LoadEmployeesAsync(connection, organizationId, filter, today, cancellationToken);
        return HeadcountSeries(rows, period);
    }

    // -------------------------------------------------------------------------
    // Workforce
    // -------------------------------------------------------------------------

    public async Task<HrWorkforceAnalyticsDto> GetWorkforceAsync(
        Guid organizationId, HrReportsFilterQuery filter, DateTime today, CancellationToken cancellationToken = default)
    {
        var period = ResolvePeriod(filter, today);
        using var connection = OpenConnection();
        var rows = await LoadEmployeesAsync(connection, organizationId, filter, today, cancellationToken);

        var hasPrev = period.PrevEnd.HasValue;
        DateTime ps = period.PrevStart ?? period.Start, pe = period.PrevEnd ?? period.End;

        var hires = HiresBetween(rows, period.Start, period.End);
        var departures = DeparturesBetween(rows, period.Start, period.End).Count;
        var prevHires = hasPrev ? HiresBetween(rows, ps, pe) : (int?)null;
        var prevDepartures = hasPrev ? DeparturesBetween(rows, ps, pe).Count : (int?)null;
        var current = rows.Where(r => InHeadcount(r, period.End)).ToList();

        return new HrWorkforceAnalyticsDto
        {
            TotalEmployees = Metric(current.Count, hasPrev ? HeadcountAt(rows, pe) : null, "employees", 0),
            ActiveEmployees = Metric(rows.Count(r => r.Status == "Active"), null, "employees", 0),
            OnLeave = Metric(rows.Count(r => r.Status == "OnLeave"), null, "employees", 0),
            NewHires = Metric(hires, prevHires, "employees", 0),
            Departures = Metric(departures, prevDepartures, "employees", 0),
            NetGrowth = Metric(hires - departures, hasPrev ? prevHires - prevDepartures : null, "employees", 0),
            AverageTenureYears = Metric(AverageTenureYears(rows, period.End), hasPrev ? AverageTenureYears(rows, pe) : null, "years"),
            RetentionRate = Metric(RetentionRate(rows, period.Start, period.End), hasPrev ? RetentionRate(rows, ps, pe) : null, "%"),
            TurnoverRate = Metric(TurnoverRate(rows, period.Start, period.End), hasPrev ? TurnoverRate(rows, ps, pe) : null, "%"),
            HeadcountEvolution = HeadcountSeries(rows, period),
            ArrivalsVsDeparturesArrivals = MonthlyCounts(
                rows.Where(r => r.HireDate.HasValue).Select(r => r.HireDate!.Value), period),
            ArrivalsVsDeparturesDepartures = MonthlyCounts(
                rows.Where(r => r.TerminationDate.HasValue && r.Status == "Terminated").Select(r => r.TerminationDate!.Value), period),
            ByDepartment = GroupNamed(current, r => r.DepartmentLabel),
            ByLocation = GroupNamed(current, r => string.IsNullOrWhiteSpace(r.LocationName) ? "Unassigned" : r.LocationName!),
            ByEmploymentType = GroupNamed(current, r => string.IsNullOrWhiteSpace(r.EmploymentTypeName) ? "Unassigned" : r.EmploymentTypeName!),
            ByStatus = GroupNamed(rows, r => r.Status),
            ByWorkModality = GroupNamed(current, r => string.IsNullOrWhiteSpace(r.Modality) ? "Unspecified" : r.Modality!),
            TenureBuckets = TenureBuckets(current, period.End)
        };
    }

    private static List<NamedCountDto> TenureBuckets(IReadOnlyList<EmpRow> current, DateTime asOf)
    {
        string[] order = ["< 1 year", "1-2 years", "2-5 years", "5-10 years", "10+ years", "Unknown"];
        string Bucket(EmpRow r)
        {
            if (!r.HireDate.HasValue) return "Unknown";
            var days = (asOf.Date - r.HireDate.Value.Date).TotalDays;
            return days < 365 ? "< 1 year" : days < 730 ? "1-2 years" : days < 1825 ? "2-5 years" : days < 3650 ? "5-10 years" : "10+ years";
        }

        var counts = current.GroupBy(Bucket).ToDictionary(g => g.Key, g => g.Count());
        var total = current.Count;
        return order.Where(counts.ContainsKey).Select(k => new NamedCountDto
        {
            Key = k,
            Label = k,
            Value = counts[k],
            Percent = total == 0 ? 0 : Math.Round(counts[k] * 100.0 / total, 1)
        }).ToList();
    }

    // -------------------------------------------------------------------------
    // Retention
    // -------------------------------------------------------------------------

    public async Task<HrRetentionAnalyticsDto> GetRetentionAsync(
        Guid organizationId, HrReportsFilterQuery filter, DateTime today, bool includeSeparationReasons,
        CancellationToken cancellationToken = default)
    {
        var period = ResolvePeriod(filter, today);
        using var connection = OpenConnection();
        var rows = await LoadEmployeesAsync(connection, organizationId, filter, today, cancellationToken);

        var hasPrev = period.PrevEnd.HasValue;
        DateTime ps = period.PrevStart ?? period.Start, pe = period.PrevEnd ?? period.End;

        var departures = DeparturesBetween(rows, period.Start, period.End);
        var tenureAtDeparture = departures.Where(r => r.HireDate.HasValue)
            .Select(r => (r.TerminationDate!.Value.Date - r.HireDate!.Value.Date).TotalDays / 365.25).ToList();
        var prevTenureAtDeparture = hasPrev
            ? DeparturesBetween(rows, ps, pe).Where(r => r.HireDate.HasValue)
                .Select(r => (r.TerminationDate!.Value.Date - r.HireDate!.Value.Date).TotalDays / 365.25).ToList()
            : null;

        var dto = new HrRetentionAnalyticsDto
        {
            TurnoverRate = Metric(TurnoverRate(rows, period.Start, period.End), hasPrev ? TurnoverRate(rows, ps, pe) : null, "%"),
            RetentionRate = Metric(RetentionRate(rows, period.Start, period.End), hasPrev ? RetentionRate(rows, ps, pe) : null, "%"),
            AverageTenureAtDeparture = Metric(tenureAtDeparture.Count == 0 ? 0 : tenureAtDeparture.Average(),
                prevTenureAtDeparture == null ? null : (prevTenureAtDeparture.Count == 0 ? 0 : prevTenureAtDeparture.Average()), "years"),
            DeparturesInPeriod = departures.Count,
            MonthlyTurnover = Months(period.Start, period.End).Select(m => new TimeSeriesPointDto
            {
                Label = MonthLabel(m.MonthStart),
                PeriodStart = m.MonthStart,
                Value = TurnoverRate(rows, m.PointStart, m.PointEnd)
            }).ToList(),
            DeparturesByDepartment = GroupNamed(departures, r => r.DepartmentLabel),
            DeparturesByEmploymentType = GroupNamed(departures, r => string.IsNullOrWhiteSpace(r.EmploymentTypeName) ? "Unassigned" : r.EmploymentTypeName!),
            RetentionMilestones = RetentionMilestones(rows, period.End)
        };

        if (includeSeparationReasons)
        {
            var p = NewParams(organizationId, today);
            p.Add("Start", period.Start);
            p.Add("End", period.End);
            var emp = EmpFilterSql(filter, "e", p);
            var reasons = await connection.QueryAsync<KeyCount>(new CommandDefinition($@"
SELECT TOP (10) COALESCE(NULLIF(LTRIM(RTRIM(f.separation_reason)), N''), N'Not specified') AS [Key], COUNT(1) AS Cnt
FROM dbo.nh_hr_employee_offboardings f
JOIN dbo.nh_hr_employees e ON e.id = f.employee_id AND e.organization_id = f.organization_id
WHERE f.organization_id = @OrganizationId AND f.status <> N'Cancelled'
  AND COALESCE(f.termination_effective_date, f.last_working_day, f.process_start_date, CAST(f.created_at AS date)) BETWEEN @Start AND @End{emp}
GROUP BY COALESCE(NULLIF(LTRIM(RTRIM(f.separation_reason)), N''), N'Not specified')
ORDER BY COUNT(1) DESC;", p, cancellationToken: cancellationToken));
            dto.SeparationReasons = ToNamed(reasons);
        }
        else
        {
            dto.SeparationReasonsRestricted = true;
            dto.SeparationReasons = [];
        }

        return dto;
    }

    private static List<NamedCountDto> RetentionMilestones(IReadOnlyList<EmpRow> rows, DateTime asOf)
    {
        (string Key, string Label, int Days)[] milestones =
            [("90d", "90 days", 90), ("6m", "6 months", 183), ("1y", "1 year", 365), ("2y", "2 years", 730)];

        var result = new List<NamedCountDto>();
        foreach (var (key, label, days) in milestones)
        {
            var eligible = rows.Where(r => r.HireDate.HasValue && r.HireDate.Value.Date.AddDays(days) <= asOf.Date).ToList();
            if (eligible.Count == 0) continue;
            var retained = eligible.Count(r => r.TerminationDate == null || r.TerminationDate.Value.Date > r.HireDate!.Value.Date.AddDays(days));
            result.Add(new NamedCountDto
            {
                Key = key,
                Label = label,
                Value = Math.Round(retained * 100.0 / eligible.Count, 1),
                Percent = Math.Round(retained * 100.0 / eligible.Count, 1)
            });
        }
        return result;
    }

    // -------------------------------------------------------------------------
    // Onboarding
    // -------------------------------------------------------------------------

    private async Task<OnboardingPerfDto> OnboardingPerfAsync(
        IDbConnection connection, Guid organizationId, Period period, HrReportsFilterQuery filter, DateTime today, CancellationToken ct)
    {
        async Task<(double Pct, int Completed, double AvgDays)> ForWindow(DateTime start, DateTime end)
        {
            var p = NewParams(organizationId, today);
            p.Add("Start", start);
            p.Add("End", end);
            var emp = EmpFilterSql(filter, "e", p);

            var tasks = await connection.QuerySingleAsync<PerfAgg>(new CommandDefinition($@"
SELECT ISNULL(SUM(CASE WHEN k.status = N'Completed' THEN 1 ELSE 0 END), 0) AS Done, COUNT(k.id) AS Total
FROM dbo.nh_hr_employee_onboardings o
JOIN dbo.nh_hr_employees e ON e.id = o.employee_id AND e.organization_id = o.organization_id
JOIN dbo.nh_hr_onboarding_tasks k ON k.organization_id = o.organization_id AND k.onboarding_id = o.id AND k.status <> N'Cancelled'
WHERE o.organization_id = @OrganizationId AND o.status <> N'Cancelled'
  AND COALESCE(o.start_date, CAST(o.created_at AS date)) BETWEEN @Start AND @End{emp};", p, cancellationToken: ct));

            var completed = await connection.QuerySingleAsync<CompletedAgg>(new CommandDefinition($@"
SELECT COUNT(1) AS Completed,
       AVG(CAST(DATEDIFF(day, COALESCE(o.start_date, CAST(o.created_at AS date)), o.actual_completion_date) AS float)) AS AvgDays
FROM dbo.nh_hr_employee_onboardings o
JOIN dbo.nh_hr_employees e ON e.id = o.employee_id AND e.organization_id = o.organization_id
WHERE o.organization_id = @OrganizationId AND o.status = N'Completed'
  AND o.actual_completion_date BETWEEN @Start AND @End{emp};", p, cancellationToken: ct));

            return (tasks.Total == 0 ? 0 : Math.Round(tasks.Done * 100.0 / tasks.Total, 1), completed.Completed, Math.Round(completed.AvgDays ?? 0, 1));
        }

        var cur = await ForWindow(period.Start, period.End);
        (double Pct, int Completed, double AvgDays)? prev = period.PrevStart.HasValue
            ? await ForWindow(period.PrevStart.Value, period.PrevEnd!.Value)
            : null;

        var pa = NewParams(organizationId, today);
        var empA = EmpFilterSql(filter, "e", pa);
        var active = await connection.ExecuteScalarAsync<int>(new CommandDefinition($@"
SELECT COUNT(1) FROM dbo.nh_hr_employee_onboardings o
JOIN dbo.nh_hr_employees e ON e.id = o.employee_id AND e.organization_id = o.organization_id
WHERE o.organization_id = @OrganizationId AND o.status IN ({ActiveOnboardingStatuses}){empA};", pa, cancellationToken: ct));

        return new OnboardingPerfDto
        {
            CompletionPct = cur.Pct,
            PreviousCompletionPct = prev?.Pct,
            AverageCompletionDays = cur.AvgDays,
            PreviousAverageCompletionDays = prev?.AvgDays,
            CompletionDaysDeltaPct = prev is { AvgDays: > 0 } ? Math.Round((cur.AvgDays - prev.Value.AvgDays) / prev.Value.AvgDays * 100.0, 1) : 0,
            Active = active,
            CompletedInPeriod = cur.Completed
        };
    }

    public async Task<HrOnboardingAnalyticsDto> GetOnboardingAsync(
        Guid organizationId, HrReportsFilterQuery filter, DateTime today, CancellationToken cancellationToken = default)
    {
        var period = ResolvePeriod(filter, today);
        using var connection = OpenConnection();
        var perf = await OnboardingPerfAsync(connection, organizationId, period, filter, today, cancellationToken);

        var p = NewParams(organizationId, today);
        p.Add("Start", period.Start);
        p.Add("End", period.End);
        var emp = EmpFilterSql(filter, "e", p);

        var overdueTasks = await connection.ExecuteScalarAsync<int>(new CommandDefinition($@"
SELECT COUNT(1) FROM dbo.nh_hr_onboarding_tasks k
JOIN dbo.nh_hr_employee_onboardings o ON o.id = k.onboarding_id AND o.organization_id = k.organization_id
JOIN dbo.nh_hr_employees e ON e.id = o.employee_id AND e.organization_id = o.organization_id
WHERE k.organization_id = @OrganizationId AND o.status IN ({ActiveOnboardingStatuses})
  AND k.status IN ({OpenTaskStatuses}) AND k.due_date < @Today{emp};", p, cancellationToken: cancellationToken));

        var overdueOnboardings = await connection.ExecuteScalarAsync<int>(new CommandDefinition($@"
SELECT COUNT(1) FROM dbo.nh_hr_employee_onboardings o
JOIN dbo.nh_hr_employees e ON e.id = o.employee_id AND e.organization_id = o.organization_id
WHERE o.organization_id = @OrganizationId AND o.status IN ({ActiveOnboardingStatuses})
  AND o.expected_completion_date < @Today{emp};", p, cancellationToken: cancellationToken));

        var avgProgress = await connection.ExecuteScalarAsync<double?>(new CommandDefinition($@"
SELECT AVG(x.pct) FROM (
    SELECT CASE WHEN tk.total = 0 THEN 0.0 ELSE tk.done * 100.0 / tk.total END AS pct
    FROM dbo.nh_hr_employee_onboardings o
    JOIN dbo.nh_hr_employees e ON e.id = o.employee_id AND e.organization_id = o.organization_id
    OUTER APPLY (SELECT COUNT(1) AS total, ISNULL(SUM(CASE WHEN k.status = N'Completed' THEN 1 ELSE 0 END), 0) AS done
                 FROM dbo.nh_hr_onboarding_tasks k
                 WHERE k.organization_id = o.organization_id AND k.onboarding_id = o.id AND k.status <> N'Cancelled') tk
    WHERE o.organization_id = @OrganizationId AND o.status IN ({ActiveOnboardingStatuses}){emp}
) x;", p, cancellationToken: cancellationToken));

        const string scope = @"o.organization_id = @OrganizationId
  AND (o.status IN (N'NotStarted', N'InProgress', N'InReview')
       OR COALESCE(o.start_date, CAST(o.created_at AS date)) BETWEEN @Start AND @End)";

        var byStatus = await connection.QueryAsync<KeyCount>(new CommandDefinition($@"
SELECT o.status AS [Key], COUNT(1) AS Cnt
FROM dbo.nh_hr_employee_onboardings o
JOIN dbo.nh_hr_employees e ON e.id = o.employee_id AND e.organization_id = o.organization_id
WHERE {scope}{emp}
GROUP BY o.status;", p, cancellationToken: cancellationToken));

        var byDepartment = await connection.QueryAsync<KeyCount>(new CommandDefinition($@"
SELECT COALESCE(d.name, N'Unassigned') AS [Key], COUNT(1) AS Cnt
FROM dbo.nh_hr_employee_onboardings o
JOIN dbo.nh_hr_employees e ON e.id = o.employee_id AND e.organization_id = o.organization_id
LEFT JOIN dbo.nh_hr_departments d ON d.id = e.department_id AND d.organization_id = e.organization_id
WHERE {scope}{emp}
GROUP BY COALESCE(d.name, N'Unassigned');", p, cancellationToken: cancellationToken));

        var completedDates = await connection.QueryAsync<DateTime>(new CommandDefinition($@"
SELECT o.actual_completion_date
FROM dbo.nh_hr_employee_onboardings o
JOIN dbo.nh_hr_employees e ON e.id = o.employee_id AND e.organization_id = o.organization_id
WHERE o.organization_id = @OrganizationId AND o.status = N'Completed'
  AND o.actual_completion_date BETWEEN @Start AND @End{emp};", p, cancellationToken: cancellationToken));

        return new HrOnboardingAnalyticsDto
        {
            Performance = perf,
            OverdueTasks = overdueTasks,
            OverdueOnboardings = overdueOnboardings,
            AverageProgressPercent = Math.Round(avgProgress ?? 0, 1),
            ByStatus = ToNamed(byStatus),
            ByDepartment = ToNamed(byDepartment),
            CompletionTrend = MonthlyCounts(completedDates, period)
        };
    }

    // -------------------------------------------------------------------------
    // Offboarding
    // -------------------------------------------------------------------------

    private const string OffboardingStartSql = "COALESCE(f.process_start_date, f.notice_date, CAST(f.created_at AS date))";

    private async Task<OffboardingPerfDto> OffboardingPerfAsync(
        IDbConnection connection, Guid organizationId, Period period, HrReportsFilterQuery filter, DateTime today, CancellationToken ct)
    {
        async Task<(double Pct, int Completed, double AvgDays)> ForWindow(DateTime start, DateTime end)
        {
            var p = NewParams(organizationId, today);
            p.Add("Start", start);
            p.Add("End", end);
            var emp = EmpFilterSql(filter, "e", p);

            var tasks = await connection.QuerySingleAsync<PerfAgg>(new CommandDefinition($@"
SELECT ISNULL(SUM(CASE WHEN k.status = N'Completed' THEN 1 ELSE 0 END), 0) AS Done, COUNT(k.id) AS Total
FROM dbo.nh_hr_employee_offboardings f
JOIN dbo.nh_hr_employees e ON e.id = f.employee_id AND e.organization_id = f.organization_id
JOIN dbo.nh_hr_offboarding_tasks k ON k.organization_id = f.organization_id AND k.offboarding_id = f.id AND k.status <> N'Cancelled'
WHERE f.organization_id = @OrganizationId AND f.status <> N'Cancelled'
  AND {OffboardingStartSql} BETWEEN @Start AND @End{emp};", p, cancellationToken: ct));

            var completed = await connection.QuerySingleAsync<CompletedAgg>(new CommandDefinition($@"
SELECT COUNT(1) AS Completed,
       AVG(CAST(DATEDIFF(day, {OffboardingStartSql}, f.actual_completion_date) AS float)) AS AvgDays
FROM dbo.nh_hr_employee_offboardings f
JOIN dbo.nh_hr_employees e ON e.id = f.employee_id AND e.organization_id = f.organization_id
WHERE f.organization_id = @OrganizationId AND f.status = N'Completed'
  AND f.actual_completion_date BETWEEN @Start AND @End{emp};", p, cancellationToken: ct));

            return (tasks.Total == 0 ? 0 : Math.Round(tasks.Done * 100.0 / tasks.Total, 1), completed.Completed, Math.Round(completed.AvgDays ?? 0, 1));
        }

        var cur = await ForWindow(period.Start, period.End);
        (double Pct, int Completed, double AvgDays)? prev = period.PrevStart.HasValue
            ? await ForWindow(period.PrevStart.Value, period.PrevEnd!.Value)
            : null;

        var pa = NewParams(organizationId, today);
        var empA = EmpFilterSql(filter, "e", pa);
        var active = await connection.ExecuteScalarAsync<int>(new CommandDefinition($@"
SELECT COUNT(1) FROM dbo.nh_hr_employee_offboardings f
JOIN dbo.nh_hr_employees e ON e.id = f.employee_id AND e.organization_id = f.organization_id
WHERE f.organization_id = @OrganizationId AND f.status IN ({ActiveOffboardingStatuses}){empA};", pa, cancellationToken: ct));

        return new OffboardingPerfDto
        {
            CompletionPct = cur.Pct,
            PreviousCompletionPct = prev?.Pct,
            AverageCompletionDays = cur.AvgDays,
            PreviousAverageCompletionDays = prev?.AvgDays,
            CompletionDaysDeltaPct = prev is { AvgDays: > 0 } ? Math.Round((cur.AvgDays - prev.Value.AvgDays) / prev.Value.AvgDays * 100.0, 1) : 0,
            Active = active,
            CompletedInPeriod = cur.Completed
        };
    }

    public async Task<HrOffboardingAnalyticsDto> GetOffboardingAsync(
        Guid organizationId, HrReportsFilterQuery filter, DateTime today, CancellationToken cancellationToken = default)
    {
        var period = ResolvePeriod(filter, today);
        using var connection = OpenConnection();
        var perf = await OffboardingPerfAsync(connection, organizationId, period, filter, today, cancellationToken);

        var p = NewParams(organizationId, today);
        p.Add("Start", period.Start);
        p.Add("End", period.End);
        var emp = EmpFilterSql(filter, "e", p);

        var pendingAssets = await connection.ExecuteScalarAsync<int>(new CommandDefinition($@"
SELECT COUNT(1) FROM dbo.nh_hr_offboarding_assets s
JOIN dbo.nh_hr_employee_offboardings f ON f.id = s.offboarding_id AND f.organization_id = s.organization_id
JOIN dbo.nh_hr_employees e ON e.id = f.employee_id AND e.organization_id = f.organization_id
WHERE s.organization_id = @OrganizationId AND s.status = N'PendingReturn' AND f.status IN ({ActiveOffboardingStatuses}){emp};",
            p, cancellationToken: cancellationToken));

        var pendingAccess = await connection.ExecuteScalarAsync<int>(new CommandDefinition($@"
SELECT COUNT(1) FROM dbo.nh_hr_offboarding_access_items s
JOIN dbo.nh_hr_employee_offboardings f ON f.id = s.offboarding_id AND f.organization_id = s.organization_id
JOIN dbo.nh_hr_employees e ON e.id = f.employee_id AND e.organization_id = f.organization_id
WHERE s.organization_id = @OrganizationId AND s.status IN (N'Pending', N'Scheduled') AND f.status IN ({ActiveOffboardingStatuses}){emp};",
            p, cancellationToken: cancellationToken));

        var pendingClearance = await connection.ExecuteScalarAsync<int>(new CommandDefinition($@"
SELECT COUNT(1) FROM dbo.nh_hr_employee_offboardings f
JOIN dbo.nh_hr_employees e ON e.id = f.employee_id AND e.organization_id = f.organization_id
WHERE f.organization_id = @OrganizationId AND f.status = N'PendingClearance'{emp};", p, cancellationToken: cancellationToken));

        string Scope() => $@"f.organization_id = @OrganizationId
  AND (f.status IN ({ActiveOffboardingStatuses}) OR {OffboardingStartSql} BETWEEN @Start AND @End)";

        async Task<IEnumerable<KeyCount>> Group(string keySql, bool joinDepartment) =>
            await connection.QueryAsync<KeyCount>(new CommandDefinition($@"
SELECT {keySql} AS [Key], COUNT(1) AS Cnt
FROM dbo.nh_hr_employee_offboardings f
JOIN dbo.nh_hr_employees e ON e.id = f.employee_id AND e.organization_id = f.organization_id
{(joinDepartment ? "LEFT JOIN dbo.nh_hr_departments d ON d.id = e.department_id AND d.organization_id = e.organization_id" : "")}
WHERE {Scope()}{emp}
GROUP BY {keySql};", p, cancellationToken: cancellationToken));

        var byStatus = await Group("f.status", false);
        var bySeparationType = await Group("f.separation_type", false);
        var byDepartment = await Group("COALESCE(d.name, N'Unassigned')", true);

        var completedDates = await connection.QueryAsync<DateTime>(new CommandDefinition($@"
SELECT f.actual_completion_date
FROM dbo.nh_hr_employee_offboardings f
JOIN dbo.nh_hr_employees e ON e.id = f.employee_id AND e.organization_id = f.organization_id
WHERE f.organization_id = @OrganizationId AND f.status = N'Completed'
  AND f.actual_completion_date BETWEEN @Start AND @End{emp};", p, cancellationToken: cancellationToken));

        return new HrOffboardingAnalyticsDto
        {
            Performance = perf,
            PendingAssetReturns = pendingAssets,
            PendingAccessRevocations = pendingAccess,
            PendingClearance = pendingClearance,
            ByStatus = ToNamed(byStatus),
            BySeparationType = ToNamed(bySeparationType),
            ByDepartment = ToNamed(byDepartment),
            CompletionTrend = MonthlyCounts(completedDates, period)
        };
    }

    // -------------------------------------------------------------------------
    // Organization
    // -------------------------------------------------------------------------

    private sealed class PositionRow
    {
        public Guid DepartmentId { get; set; }
        public string DepartmentName { get; set; } = "";
        public int Approved { get; set; }
        public int Filled { get; set; }
    }

    public async Task<HrOrganizationAnalyticsDto> GetOrganizationAsync(
        Guid organizationId, HrReportsFilterQuery filter, DateTime today, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var employees = (await LoadEmployeesAsync(connection, organizationId, filter, today, cancellationToken))
            .Where(r => r.Status != "Terminated").ToList();

        var p = NewParams(organizationId, today);
        var posFilter = new StringBuilder();
        if (filter.DepartmentId.HasValue) { posFilter.Append(" AND p.department_id = @FDepartmentId"); p.Add("FDepartmentId", filter.DepartmentId.Value); }
        if (filter.WorkLocationId.HasValue) { posFilter.Append(" AND p.work_location_id = @FLocationId"); p.Add("FLocationId", filter.WorkLocationId.Value); }
        if (filter.OrganizationalLevelId.HasValue) { posFilter.Append(" AND p.organizational_level_id = @FLevelId"); p.Add("FLevelId", filter.OrganizationalLevelId.Value); }

        var positions = (await connection.QueryAsync<PositionRow>(new CommandDefinition($@"
SELECT p.department_id AS DepartmentId, d.name AS DepartmentName, p.approved_headcount AS Approved, fc.n AS Filled
FROM dbo.nh_hr_organizational_positions p
JOIN dbo.nh_hr_departments d ON d.id = p.department_id AND d.organization_id = p.organization_id
CROSS APPLY (SELECT COUNT(1) AS n FROM dbo.nh_hr_employee_position_assignments a
             WHERE a.organization_id = p.organization_id AND a.position_id = p.id
               AND a.end_date IS NULL AND a.status = N'Active') fc
WHERE p.organization_id = @OrganizationId AND p.archived_at IS NULL AND p.status <> N'Archived'{posFilter};",
            p, cancellationToken: cancellationToken))).ToList();

        var deptRow = await connection.QuerySingleAsync<(int Total, int Teams)>(new CommandDefinition(@"
SELECT COUNT(1) AS Total, ISNULL(SUM(CASE WHEN parent_department_id IS NOT NULL THEN 1 ELSE 0 END), 0) AS Teams
FROM dbo.nh_hr_departments
WHERE organization_id = @OrganizationId AND archived_at IS NULL AND is_active = 1;",
            new { OrganizationId = organizationId }, cancellationToken: cancellationToken));

        var approved = positions.Sum(x => x.Approved);
        var seated = positions.Sum(x => Math.Min(x.Filled, x.Approved));

        // Span of control & depth over the (filtered) active workforce.
        var ids = employees.Select(e => e.Id).ToHashSet();
        var reportCounts = employees.Where(e => e.ManagerId.HasValue && ids.Contains(e.ManagerId.Value))
            .GroupBy(e => e.ManagerId!.Value).Select(g => g.Count()).ToList();

        return new HrOrganizationAnalyticsDto
        {
            TotalDepartments = deptRow.Total,
            TotalTeams = deptRow.Teams,
            TotalPositions = positions.Count,
            ApprovedHeadcount = approved,
            ActualHeadcount = employees.Count,
            OccupiedPositions = positions.Count(x => x.Filled > 0),
            VacantPositions = positions.Count(x => x.Filled < x.Approved),
            HeadcountUtilizationPct = approved == 0 ? 0 : Math.Round(seated * 100.0 / approved, 1),
            AverageSpanOfControl = reportCounts.Count == 0 ? 0 : Math.Round(reportCounts.Average(), 1),
            OrganizationalDepth = Depth(employees),
            EmployeesWithoutManager = employees.Count(e => !e.ManagerId.HasValue),
            EmployeesWithoutPosition = employees.Count(e => !e.HasPosition),
            UtilizationByDepartment = positions.GroupBy(x => x.DepartmentName, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var appr = g.Sum(x => x.Approved);
                    var pct = appr == 0 ? 0 : Math.Round(g.Sum(x => Math.Min(x.Filled, x.Approved)) * 100.0 / appr, 1);
                    return new NamedCountDto { Key = g.Key, Label = g.Key, Value = pct, Percent = pct };
                })
                .OrderByDescending(x => x.Value).ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase).ToList(),
            VacanciesByDepartment = positions.GroupBy(x => x.DepartmentName, StringComparer.OrdinalIgnoreCase)
                .Select(g => new NamedCountDto
                {
                    Key = g.Key,
                    Label = g.Key,
                    Value = g.Sum(x => Math.Max(x.Approved - x.Filled, 0))
                })
                .Where(x => x.Value > 0)
                .OrderByDescending(x => x.Value).ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    /// <summary>Longest manager chain (roots = 1). Cycle-safe.</summary>
    private static int Depth(IReadOnlyList<EmpRow> employees)
    {
        if (employees.Count == 0) return 0;
        var managerOf = employees.ToDictionary(e => e.Id, e => e.ManagerId);
        var memo = new Dictionary<Guid, int>();

        int DepthOf(Guid id)
        {
            if (memo.TryGetValue(id, out var cached)) return cached;
            var visited = new HashSet<Guid>();
            var chain = new List<Guid>();
            var current = (Guid?)id;
            var baseDepth = 0;
            while (current.HasValue && managerOf.ContainsKey(current.Value))
            {
                if (memo.TryGetValue(current.Value, out var known)) { baseDepth = known; break; }
                if (!visited.Add(current.Value)) break; // cycle
                chain.Add(current.Value);
                current = managerOf[current.Value];
            }

            var depth = baseDepth;
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                depth++;
                memo[chain[i]] = depth;
            }
            return memo.TryGetValue(id, out var result) ? result : 1;
        }

        return employees.Max(e => DepthOf(e.Id));
    }

    // -------------------------------------------------------------------------
    // Documents
    // -------------------------------------------------------------------------

    private sealed class DocAgg
    {
        public int Total { get; set; }
        public int Valid { get; set; }
        public int Expired { get; set; }
        public int Exp7 { get; set; }
        public int Exp30 { get; set; }
        public int Exp60 { get; set; }
    }

    private sealed record DocumentsAggregate(DocumentsComplianceDto Compliance, int Exp7, int Exp60);

    private const string DocumentScopeSql = @"x.organization_id = @OrganizationId AND x.archived_at IS NULL AND x.status <> N'Archived'
  AND e.archived_at IS NULL AND e.employment_status <> N'Terminated'";

    private const string ValidDocumentSql = "(x.status = N'Active' AND (x.expires_at IS NULL OR x.expires_at >= @Today))";

    private async Task<DocumentsAggregate> DocumentsAggregateAsync(
        IDbConnection connection, Guid organizationId, HrReportsFilterQuery filter, DateTime today, CancellationToken ct)
    {
        var p = NewParams(organizationId, today);
        var emp = EmpFilterSql(filter, "e", p);

        var agg = await connection.QuerySingleAsync<DocAgg>(new CommandDefinition($@"
SELECT COUNT(1) AS Total,
       ISNULL(SUM(CASE WHEN {ValidDocumentSql} THEN 1 ELSE 0 END), 0) AS Valid,
       ISNULL(SUM(CASE WHEN x.status = N'Expired' OR x.expires_at < @Today THEN 1 ELSE 0 END), 0) AS Expired,
       ISNULL(SUM(CASE WHEN x.status <> N'Expired' AND x.expires_at >= @Today AND x.expires_at <= DATEADD(day, 7, @Today) THEN 1 ELSE 0 END), 0) AS Exp7,
       ISNULL(SUM(CASE WHEN x.status <> N'Expired' AND x.expires_at >= @Today AND x.expires_at <= DATEADD(day, 30, @Today) THEN 1 ELSE 0 END), 0) AS Exp30,
       ISNULL(SUM(CASE WHEN x.status <> N'Expired' AND x.expires_at >= @Today AND x.expires_at <= DATEADD(day, 60, @Today) THEN 1 ELSE 0 END), 0) AS Exp60
FROM dbo.nh_hr_employee_documents x
JOIN dbo.nh_hr_employees e ON e.id = x.employee_id AND e.organization_id = x.organization_id
WHERE {DocumentScopeSql}{emp};", p, cancellationToken: ct));

        var openRequests = await connection.ExecuteScalarAsync<int>(new CommandDefinition($@"
SELECT COUNT(1) FROM dbo.nh_hr_document_requests r
JOIN dbo.nh_hr_employees e ON e.id = r.employee_id AND e.organization_id = r.organization_id
WHERE r.organization_id = @OrganizationId AND r.status IN (N'Open', N'Overdue')
  AND e.archived_at IS NULL AND e.employment_status <> N'Terminated'{emp};", p, cancellationToken: ct));

        // Completeness: valid documents on file vs. documents on file plus outstanding requests.
        var denominator = agg.Total + openRequests;
        var compliance = new DocumentsComplianceDto
        {
            CompletenessPct = denominator == 0 ? 0 : Math.Round(agg.Valid * 100.0 / denominator, 1),
            TotalDocuments = agg.Total,
            ValidDocuments = agg.Valid,
            Expired = agg.Expired,
            ExpiringIn30Days = agg.Exp30,
            OpenRequests = openRequests
        };
        return new DocumentsAggregate(compliance, agg.Exp7, agg.Exp60);
    }

    public async Task<HrDocumentsAnalyticsDto> GetDocumentsAsync(
        Guid organizationId, HrReportsFilterQuery filter, DateTime today, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var agg = await DocumentsAggregateAsync(connection, organizationId, filter, today, cancellationToken);

        var p = NewParams(organizationId, today);
        var emp = EmpFilterSql(filter, "e", p);

        var byCategory = await connection.QueryAsync<KeyCount>(new CommandDefinition($@"
SELECT x.category AS [Key], COUNT(1) AS Cnt
FROM dbo.nh_hr_employee_documents x
JOIN dbo.nh_hr_employees e ON e.id = x.employee_id AND e.organization_id = x.organization_id
WHERE {DocumentScopeSql}{emp}
GROUP BY x.category;", p, cancellationToken: cancellationToken));

        var byDepartment = (await connection.QueryAsync<KeyCount>(new CommandDefinition($@"
SELECT COALESCE(d.name, N'Unassigned') AS [Key], COUNT(1) AS Cnt,
       ISNULL(SUM(CASE WHEN {ValidDocumentSql} THEN 1 ELSE 0 END), 0) AS Valid
FROM dbo.nh_hr_employee_documents x
JOIN dbo.nh_hr_employees e ON e.id = x.employee_id AND e.organization_id = x.organization_id
LEFT JOIN dbo.nh_hr_departments d ON d.id = e.department_id AND d.organization_id = e.organization_id
WHERE {DocumentScopeSql}{emp}
GROUP BY COALESCE(d.name, N'Unassigned');", p, cancellationToken: cancellationToken))).ToList();

        return new HrDocumentsAnalyticsDto
        {
            Compliance = agg.Compliance,
            ByCategory = ToNamed(byCategory),
            CompletenessByDepartment = byDepartment
                .Select(r =>
                {
                    var pct = r.Cnt == 0 ? 0 : Math.Round(r.Valid * 100.0 / r.Cnt, 1);
                    return new NamedCountDto { Key = r.Key, Label = r.Key, Value = pct, Percent = pct };
                })
                .OrderBy(x => x.Value).ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase).ToList(),
            ExpiringIn7Days = agg.Exp7,
            ExpiringIn60Days = agg.Exp60
        };
    }

    // -------------------------------------------------------------------------
    // Compensation
    // -------------------------------------------------------------------------

    private sealed class CompRow
    {
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "";
        public string PayFrequency { get; set; } = "";
        public string Department { get; set; } = "";
        public string JobTitle { get; set; } = "";
        public double Annualized => (double)Amount * AnnualFactor(PayFrequency);
    }

    /// <summary>Rough annualization used for aggregates only (Hourly 2080 h, Weekly 52, BiWeekly 26, SemiMonthly 24, Monthly 12).</summary>
    internal static double AnnualFactor(string payFrequency) => payFrequency switch
    {
        "Hourly" => 2080,
        "Weekly" => 52,
        "BiWeekly" => 26,
        "SemiMonthly" => 24,
        "Monthly" => 12,
        _ => 1
    };

    public async Task<HrCompensationAnalyticsDto> GetCompensationAsync(
        Guid organizationId, HrReportsFilterQuery filter, DateTime today, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var p = NewParams(organizationId, today);
        var emp = EmpFilterSql(filter, "e", p);

        var rows = (await connection.QueryAsync<CompRow>(new CommandDefinition($@"
SELECT c.amount AS Amount, c.currency AS Currency, c.pay_frequency AS PayFrequency,
       COALESCE(d.name, N'Unassigned') AS Department, COALESCE(jt.name, N'Unassigned') AS JobTitle
FROM dbo.nh_hr_employee_compensation c
JOIN dbo.nh_hr_employees e ON e.id = c.employee_id AND e.organization_id = c.organization_id
LEFT JOIN dbo.nh_hr_departments d ON d.id = e.department_id AND d.organization_id = e.organization_id
LEFT JOIN dbo.nh_hr_job_titles jt ON jt.id = e.job_title_id AND jt.organization_id = e.organization_id
WHERE c.organization_id = @OrganizationId AND e.archived_at IS NULL AND e.employment_status <> N'Terminated'
  AND c.effective_date <= @Today AND (c.end_date IS NULL OR c.end_date >= @Today)
  AND NOT EXISTS (SELECT 1 FROM dbo.nh_hr_employee_compensation c2
                  WHERE c2.organization_id = c.organization_id AND c2.employee_id = c.employee_id
                    AND c2.effective_date <= @Today AND c2.effective_date > c.effective_date){emp};",
            p, cancellationToken: cancellationToken))).ToList();

        var dto = new HrCompensationAnalyticsDto { EmployeeCount = rows.Count };

        if (rows.Count == 0)
        {
            dto.Available = false;
            dto.UnavailableReason = "No compensation data is available for the selected scope.";
            return dto;
        }

        if (rows.Count < MinimumCohortSize)
        {
            dto.Available = false;
            dto.RestrictedByCohortSize = true;
            dto.UnavailableReason = $"Compensation aggregates require at least {MinimumCohortSize} employees.";
            return dto;
        }

        var currencies = rows.Select(r => (r.Currency ?? "").Trim().ToUpperInvariant()).Distinct().ToList();
        if (currencies.Count > 1)
        {
            dto.Available = false;
            dto.UnavailableReason = "Compensation is recorded in multiple currencies; aggregates require a single currency.";
            return dto;
        }

        var values = rows.Select(r => r.Annualized).OrderBy(v => v).ToList();
        var mid = values.Count / 2;
        var median = values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2.0;

        List<NamedCountDto> Group(Func<CompRow, string> key, int? take)
        {
            var groups = rows.GroupBy(key, StringComparer.OrdinalIgnoreCase).ToList();
            // Groups below the cohort size are suppressed so individuals cannot be inferred.
            if (groups.Any(g => g.Count() < MinimumCohortSize)) dto.RestrictedByCohortSize = true;
            var q = groups.Where(g => g.Count() >= MinimumCohortSize)
                .Select(g => new NamedCountDto { Key = g.Key, Label = g.Key, Value = Math.Round(g.Average(r => r.Annualized), 0) })
                .OrderByDescending(x => x.Value).ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase);
            return (take.HasValue ? q.Take(take.Value) : q).ToList();
        }

        dto.Available = true;
        dto.Currency = currencies[0];
        dto.AverageAnnualized = Math.Round(values.Average(), 0);
        dto.MedianAnnualized = Math.Round(median, 0);
        dto.ByDepartment = Group(r => r.Department, null);
        dto.ByJobTitle = Group(r => r.JobTitle, 15);
        return dto;
    }
}
