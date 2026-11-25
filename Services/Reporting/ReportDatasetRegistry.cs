using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using nexthire_api.DTOs.CoreHr;
using nexthire_api.Security;

namespace nexthire_api.Services.Reporting;

// ---------------------------------------------------------------------------
// Dataset metadata (allowlist). SQL expressions live ONLY here and are never built from user input.
// ---------------------------------------------------------------------------

/// <summary>One reportable field. <see cref="SqlExpression"/> is a trusted fragment owned by the registry.</summary>
public sealed record ReportFieldDef(
    string Key,
    string Label,
    string DataType,
    string SqlExpression,
    bool IsDimension = false,
    bool IsMeasure = false,
    bool Filterable = true,
    string? RequiredPermission = null)
{
    /// <summary>Aggregations allowed for this field (count/distinctCount always, sum/avg for numbers, min/max for numbers and dates).</summary>
    public IReadOnlyCollection<string> AllowedAggregations
    {
        get
        {
            var list = new List<string> { ReportAggregations.Count, ReportAggregations.DistinctCount };
            if (DataType == ReportDataTypes.Number)
            {
                list.Add(ReportAggregations.Sum);
                list.Add(ReportAggregations.Avg);
            }
            if (DataType is ReportDataTypes.Number or ReportDataTypes.Date)
            {
                list.Add(ReportAggregations.Min);
                list.Add(ReportAggregations.Max);
            }
            return list;
        }
    }
}

/// <summary>A Core HR dataset: one fixed FROM clause (all joins tenant-scoped) plus its allowlisted fields.</summary>
public sealed class ReportDatasetDef
{
    private readonly Dictionary<string, ReportFieldDef> _byKey;

    public ReportDatasetDef(
        string key, string name, string description, string requiredPermission,
        string organizationColumn, string fromSql, string? baseWhereSql,
        IReadOnlyList<ReportFieldDef> fields, IReadOnlyList<string> defaultColumns,
        string? dateExpression = null, string? departmentExpression = null, int minCohortSize = 0)
    {
        Key = key;
        Name = name;
        Description = description;
        RequiredPermission = requiredPermission;
        OrganizationColumn = organizationColumn;
        FromSql = fromSql;
        BaseWhereSql = baseWhereSql;
        Fields = fields;
        DefaultColumns = defaultColumns;
        DateExpression = dateExpression;
        DepartmentExpression = departmentExpression;
        MinCohortSize = minCohortSize;
        _byKey = fields.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
    }

    public string Key { get; }
    public string Name { get; }
    public string Description { get; }
    public string RequiredPermission { get; }

    /// <summary>Fully qualified tenant column (e.g. <c>e.organization_id</c>) always constrained to <c>@OrganizationId</c>.</summary>
    public string OrganizationColumn { get; }

    public string FromSql { get; }
    public string? BaseWhereSql { get; }
    public IReadOnlyList<ReportFieldDef> Fields { get; }
    public IReadOnlyList<string> DefaultColumns { get; }

    /// <summary>Date expression used by the optional From/To parameters (null = dataset ignores them).</summary>
    public string? DateExpression { get; }

    /// <summary>Department id expression used by the optional DepartmentId parameter (null = ignored).</summary>
    public string? DepartmentExpression { get; }

    /// <summary>Minimum number of rows behind any aggregated output row (privacy threshold; 0 = none).</summary>
    public int MinCohortSize { get; }

    public ReportFieldDef? GetField(string key) => _byKey.TryGetValue(key, out var f) ? f : null;
}

public static class ReportDataTypes
{
    public const string String = "string";
    public const string Number = "number";
    public const string Date = "date";
    public const string Boolean = "boolean";
}

public static class ReportAggregations
{
    public const string Count = "count";
    public const string DistinctCount = "distinctCount";
    public const string Sum = "sum";
    public const string Avg = "avg";
    public const string Min = "min";
    public const string Max = "max";

    public static readonly IReadOnlyList<string> All = [Count, DistinctCount, Sum, Avg, Min, Max];

    public static string? Normalize(string? value) =>
        All.FirstOrDefault(a => a.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

public static class ReportFilterOperators
{
    public const string Eq = "eq";
    public const string Neq = "neq";
    public const string Gt = "gt";
    public const string Gte = "gte";
    public const string Lt = "lt";
    public const string Lte = "lte";
    public const string Contains = "contains";
    public const string StartsWith = "startsWith";
    public const string In = "in";
    public const string IsNull = "isNull";
    public const string IsNotNull = "isNotNull";

    public static readonly IReadOnlyList<string> All =
        [Eq, Neq, Gt, Gte, Lt, Lte, Contains, StartsWith, In, IsNull, IsNotNull];

    public static string? Normalize(string? value) =>
        All.FirstOrDefault(a => a.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

// ---------------------------------------------------------------------------
// Parsed, validated configuration
// ---------------------------------------------------------------------------

public sealed record ReportMeasure(string Field, string Agg, string OutputKey);

public sealed record ReportFilter(string Field, string Operator, IReadOnlyList<object?> Values);

public sealed record ReportSort(string Field, bool Descending);

public sealed class ReportConfiguration
{
    public IReadOnlyList<string> Dimensions { get; init; } = [];
    public IReadOnlyList<string> Columns { get; init; } = [];
    public IReadOnlyList<ReportMeasure> Measures { get; init; } = [];
    public IReadOnlyList<ReportFilter> Filters { get; init; } = [];
    public IReadOnlyList<ReportSort> Sort { get; init; } = [];
    public string ChartType { get; init; } = "table";
    public int Limit { get; init; } = ReportDatasetRegistry.DefaultLimit;

    /// <summary>True when measures are present (GROUP BY query); otherwise a detail (row-level) query.</summary>
    public bool IsAggregated => Measures.Count > 0;
}

public sealed record ReportConfigurationValidation(bool IsValid, IReadOnlyList<string> Errors, ReportConfiguration? Configuration)
{
    public static ReportConfigurationValidation Fail(params string[] errors) => new(false, errors, null);
}

/// <summary>
/// Static allowlist of Core HR report datasets. Recruitment (jobs, candidates, applications, pipeline) is intentionally absent.
/// </summary>
public static class ReportDatasetRegistry
{
    public const int DefaultLimit = 500;
    public const int MaxLimit = 2000;
    public const int MaxDimensions = 3;
    public const int MaxMeasures = 8;
    public const int MaxColumns = 30;
    public const int MaxFilters = 20;
    public const int MaxSortItems = 3;
    public const int MaxInValues = 50;
    public const int MaxConfigurationChars = 20_000;
    public const int CompensationMinCohort = 5;

    public static readonly IReadOnlySet<string> ChartTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "table", "bar", "column", "stackedBar", "line", "area", "pie", "donut", "number"
    };

    private static readonly Regex KeyPattern = new("^[A-Za-z][A-Za-z0-9_]{0,63}$", RegexOptions.Compiled);

    private static readonly IReadOnlyList<ReportDatasetDef> Datasets = BuildDatasets();
    private static readonly Dictionary<string, ReportDatasetDef> ByKey =
        Datasets.ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<ReportDatasetDef> GetAll() => Datasets;

    public static ReportDatasetDef? Get(string? key) =>
        !string.IsNullOrWhiteSpace(key) && ByKey.TryGetValue(key.Trim(), out var d) ? d : null;

    // -------------------------------------------------------------------------
    // DTO mapping
    // -------------------------------------------------------------------------

    /// <summary>Maps to the public DTO, hiding fields the caller is not allowed to use.</summary>
    public static ReportDatasetDto ToDto(ReportDatasetDef dataset, Func<string, bool>? hasPermission = null) => new()
    {
        Key = dataset.Key,
        Name = dataset.Name,
        Description = dataset.Description,
        RequiredPermission = dataset.RequiredPermission,
        Fields = dataset.Fields
            .Where(f => f.RequiredPermission == null || hasPermission == null || hasPermission(f.RequiredPermission))
            .Select(f => new ReportFieldDto
            {
                Key = f.Key,
                Label = f.Label,
                DataType = f.DataType,
                IsDimension = f.IsDimension,
                IsMeasure = f.IsMeasure,
                Filterable = f.Filterable,
                RequiredPermission = f.RequiredPermission
            })
            .ToList()
    };

    // -------------------------------------------------------------------------
    // Validation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Parses and validates a configuration such as
    /// <c>{ "dimensions": ["department"], "measures": [{"field":"id","agg":"count"}], "filters": [], "sort": [], "chartType": "bar", "limit": 500 }</c>.
    /// Unknown fields / aggregations / operators are rejected. <paramref name="hasPermission"/> gates restricted fields.
    /// </summary>
    public static ReportConfigurationValidation ValidateConfiguration(
        ReportDatasetDef dataset, string? configurationJson, Func<string, bool>? hasPermission = null)
    {
        if (string.IsNullOrWhiteSpace(configurationJson)) configurationJson = "{}";
        if (configurationJson.Length > MaxConfigurationChars)
            return ReportConfigurationValidation.Fail($"Configuration is too large (max {MaxConfigurationChars} characters).");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(configurationJson);
        }
        catch (JsonException)
        {
            return ReportConfigurationValidation.Fail("Configuration must be valid JSON.");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ReportConfigurationValidation.Fail("Configuration must be a JSON object.");

            var errors = new List<string>();
            bool Allowed(ReportFieldDef f) => f.RequiredPermission == null || hasPermission == null || hasPermission(f.RequiredPermission);

            ReportFieldDef? Resolve(string? key, string context)
            {
                if (string.IsNullOrWhiteSpace(key) || !KeyPattern.IsMatch(key.Trim()))
                {
                    errors.Add($"Invalid field name in {context}.");
                    return null;
                }
                var f = dataset.GetField(key.Trim());
                if (f == null)
                {
                    errors.Add($"Unknown field '{Truncate(key, 64)}' in {context} for dataset '{dataset.Key}'.");
                    return null;
                }
                if (!Allowed(f))
                {
                    errors.Add($"Field '{f.Key}' requires additional permissions.");
                    return null;
                }
                return f;
            }

            var dimensions = ReadStringArray(root, "dimensions", MaxDimensions, errors);
            var columns = ReadStringArray(root, "columns", MaxColumns, errors);

            // Dimensions
            var dimensionKeys = new List<string>();
            foreach (var d in dimensions)
            {
                var f = Resolve(d, "dimensions");
                if (f == null) continue;
                if (!dimensionKeys.Contains(f.Key, StringComparer.OrdinalIgnoreCase)) dimensionKeys.Add(f.Key);
            }

            // Measures
            var measures = new List<ReportMeasure>();
            if (root.TryGetProperty("measures", out var measuresEl) && measuresEl.ValueKind != JsonValueKind.Null)
            {
                if (measuresEl.ValueKind != JsonValueKind.Array)
                {
                    errors.Add("'measures' must be an array.");
                }
                else if (measuresEl.GetArrayLength() > MaxMeasures)
                {
                    errors.Add($"At most {MaxMeasures} measures are allowed.");
                }
                else
                {
                    var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var m in measuresEl.EnumerateArray())
                    {
                        // Accept either { "field": "id", "agg": "count" } or a bare field key string (defaults to count).
                        string? fieldKey;
                        string? aggRaw;
                        if (m.ValueKind == JsonValueKind.String)
                        {
                            fieldKey = m.GetString();
                            aggRaw = ReportAggregations.Count;
                        }
                        else if (m.ValueKind == JsonValueKind.Object)
                        {
                            fieldKey = ReadString(m, "field");
                            aggRaw = ReadString(m, "agg");
                            if (string.IsNullOrWhiteSpace(aggRaw)) aggRaw = ReportAggregations.Count;
                        }
                        else
                        {
                            errors.Add("Each measure must be a field key string or an object with 'field' and 'agg'.");
                            continue;
                        }

                        var f = Resolve(fieldKey, "measures");
                        var agg = ReportAggregations.Normalize(aggRaw);
                        if (agg == null)
                        {
                            errors.Add($"Unknown aggregation '{Truncate(aggRaw, 32)}'. Allowed: {string.Join(", ", ReportAggregations.All)}.");
                            continue;
                        }
                        if (f == null) continue;
                        if (!f.AllowedAggregations.Contains(agg))
                        {
                            errors.Add($"Aggregation '{agg}' is not allowed on field '{f.Key}'.");
                            continue;
                        }

                        var outputKey = $"{agg}_{f.Key}";
                        if (used.TryGetValue(outputKey, out var n))
                        {
                            used[outputKey] = n + 1;
                            outputKey = $"{outputKey}_{n + 1}";
                        }
                        else
                        {
                            used[outputKey] = 1;
                        }
                        measures.Add(new ReportMeasure(f.Key, agg, outputKey));
                    }
                }
            }

            // Aggregated queries group by dimensions: they must be dimension fields.
            if (measures.Count > 0)
            {
                foreach (var key in dimensionKeys)
                {
                    if (!dataset.GetField(key)!.IsDimension)
                        errors.Add($"Field '{key}' cannot be used as a dimension.");
                }
            }

            // Detail columns (only used without measures)
            var columnKeys = new List<string>();
            if (measures.Count == 0)
            {
                foreach (var key in columns.Concat(dimensionKeys))
                {
                    var f = Resolve(key, "columns");
                    if (f != null && !columnKeys.Contains(f.Key, StringComparer.OrdinalIgnoreCase)) columnKeys.Add(f.Key);
                }
                if (columnKeys.Count == 0)
                    columnKeys.AddRange(dataset.DefaultColumns.Where(k => dataset.GetField(k) is { } f && Allowed(f)));
            }

            // Filters
            var filters = new List<ReportFilter>();
            if (root.TryGetProperty("filters", out var filtersEl) && filtersEl.ValueKind != JsonValueKind.Null)
            {
                if (filtersEl.ValueKind != JsonValueKind.Array)
                {
                    errors.Add("'filters' must be an array.");
                }
                else if (filtersEl.GetArrayLength() > MaxFilters)
                {
                    errors.Add($"At most {MaxFilters} filters are allowed.");
                }
                else
                {
                    foreach (var fe in filtersEl.EnumerateArray())
                    {
                        if (fe.ValueKind != JsonValueKind.Object)
                        {
                            errors.Add("Each filter must be an object with 'field', 'op' and 'value'.");
                            continue;
                        }

                        var f = Resolve(ReadString(fe, "field"), "filters");
                        var opRaw = ReadString(fe, "op") ?? ReadString(fe, "operator");
                        var op = ReportFilterOperators.Normalize(opRaw);
                        if (op == null)
                        {
                            errors.Add($"Unknown filter operator '{Truncate(opRaw, 32)}'. Allowed: {string.Join(", ", ReportFilterOperators.All)}.");
                            continue;
                        }
                        if (f == null) continue;
                        if (!f.Filterable)
                        {
                            errors.Add($"Field '{f.Key}' cannot be filtered.");
                            continue;
                        }

                        if (op is ReportFilterOperators.IsNull or ReportFilterOperators.IsNotNull)
                        {
                            filters.Add(new ReportFilter(f.Key, op, []));
                            continue;
                        }

                        if ((op is ReportFilterOperators.Contains or ReportFilterOperators.StartsWith) && f.DataType != ReportDataTypes.String)
                        {
                            errors.Add($"Operator '{op}' is only valid on text fields ('{f.Key}').");
                            continue;
                        }
                        if (op is ReportFilterOperators.Gt or ReportFilterOperators.Gte or ReportFilterOperators.Lt or ReportFilterOperators.Lte
                            && f.DataType is ReportDataTypes.Boolean)
                        {
                            errors.Add($"Operator '{op}' is not valid on field '{f.Key}'.");
                            continue;
                        }

                        if (!fe.TryGetProperty("value", out var valueEl) && !fe.TryGetProperty("values", out valueEl))
                        {
                            errors.Add($"Filter on '{f.Key}' requires a value.");
                            continue;
                        }

                        var values = new List<object?>();
                        var ok = true;
                        if (op == ReportFilterOperators.In)
                        {
                            if (valueEl.ValueKind != JsonValueKind.Array || valueEl.GetArrayLength() == 0 || valueEl.GetArrayLength() > MaxInValues)
                            {
                                errors.Add($"Filter 'in' on '{f.Key}' requires an array with 1-{MaxInValues} values.");
                                continue;
                            }
                            foreach (var item in valueEl.EnumerateArray())
                            {
                                if (!TryConvertValue(item, f.DataType, out var converted))
                                {
                                    ok = false;
                                    break;
                                }
                                values.Add(converted);
                            }
                        }
                        else
                        {
                            if (!TryConvertValue(valueEl, f.DataType, out var converted)) ok = false;
                            else values.Add(converted);
                        }

                        if (!ok)
                        {
                            errors.Add($"Invalid value for filter on '{f.Key}' (expected {f.DataType}).");
                            continue;
                        }

                        filters.Add(new ReportFilter(f.Key, op, values));
                    }
                }
            }

            // Sort
            var outputKeys = measures.Count > 0
                ? dimensionKeys.Concat(measures.Select(m => m.OutputKey)).ToList()
                : columnKeys;
            var sort = new List<ReportSort>();
            if (root.TryGetProperty("sort", out var sortEl) && sortEl.ValueKind != JsonValueKind.Null)
            {
                if (sortEl.ValueKind != JsonValueKind.Array)
                {
                    errors.Add("'sort' must be an array.");
                }
                else if (sortEl.GetArrayLength() > MaxSortItems)
                {
                    errors.Add($"At most {MaxSortItems} sort items are allowed.");
                }
                else
                {
                    foreach (var se in sortEl.EnumerateArray())
                    {
                        string? field;
                        var desc = false;
                        if (se.ValueKind == JsonValueKind.String)
                        {
                            field = se.GetString();
                        }
                        else if (se.ValueKind == JsonValueKind.Object)
                        {
                            field = ReadString(se, "field");
                            var dir = ReadString(se, "dir") ?? ReadString(se, "direction");
                            if (dir != null && !dir.Equals("asc", StringComparison.OrdinalIgnoreCase) && !dir.Equals("desc", StringComparison.OrdinalIgnoreCase))
                            {
                                errors.Add("Sort direction must be 'asc' or 'desc'.");
                                continue;
                            }
                            desc = dir != null && dir.Equals("desc", StringComparison.OrdinalIgnoreCase);
                        }
                        else
                        {
                            errors.Add("Each sort item must be a field name or an object with 'field' and 'dir'.");
                            continue;
                        }

                        var match = outputKeys.FirstOrDefault(k => k.Equals(field?.Trim(), StringComparison.OrdinalIgnoreCase));
                        if (match == null)
                        {
                            errors.Add($"Sort field '{Truncate(field, 64)}' must be one of the selected columns/measures.");
                            continue;
                        }
                        sort.Add(new ReportSort(match, desc));
                    }
                }
            }

            // Chart type
            var chartType = "table";
            var chartRaw = ReadString(root, "chartType");
            if (!string.IsNullOrWhiteSpace(chartRaw))
            {
                var match = ChartTypes.FirstOrDefault(c => c.Equals(chartRaw.Trim(), StringComparison.OrdinalIgnoreCase));
                if (match == null) errors.Add($"Unknown chart type '{Truncate(chartRaw, 32)}'.");
                else chartType = match;
            }

            // Limit
            var limit = DefaultLimit;
            if (root.TryGetProperty("limit", out var limitEl) && limitEl.ValueKind != JsonValueKind.Null)
            {
                if (limitEl.ValueKind != JsonValueKind.Number || !limitEl.TryGetInt32(out limit) || limit < 1 || limit > MaxLimit)
                {
                    errors.Add($"'limit' must be an integer between 1 and {MaxLimit}.");
                    limit = DefaultLimit;
                }
            }

            if (errors.Count > 0)
                return new ReportConfigurationValidation(false, errors, null);

            return new ReportConfigurationValidation(true, [], new ReportConfiguration
            {
                Dimensions = dimensionKeys,
                Columns = columnKeys,
                Measures = measures,
                Filters = filters,
                Sort = sort,
                ChartType = chartType,
                Limit = limit
            });
        }
    }

    // -------------------------------------------------------------------------
    // JSON helpers
    // -------------------------------------------------------------------------

    private static string? ReadString(JsonElement element, string name)
    {
        foreach (var p in element.EnumerateObject())
        {
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
        }
        return null;
    }

    private static List<string> ReadStringArray(JsonElement root, string name, int max, List<string> errors)
    {
        var result = new List<string>();
        foreach (var p in root.EnumerateObject())
        {
            if (!p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || p.Value.ValueKind == JsonValueKind.Null) continue;
            if (p.Value.ValueKind != JsonValueKind.Array)
            {
                errors.Add($"'{name}' must be an array of field names.");
                return result;
            }
            if (p.Value.GetArrayLength() > max)
            {
                errors.Add($"At most {max} entries are allowed in '{name}'.");
                return result;
            }
            foreach (var item in p.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    errors.Add($"'{name}' must only contain field names.");
                    return result;
                }
                result.Add(item.GetString() ?? "");
            }
        }
        return result;
    }

    private static bool TryConvertValue(JsonElement el, string dataType, out object? value)
    {
        value = null;
        switch (dataType)
        {
            case ReportDataTypes.Number:
                if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var dec)) { value = dec; return true; }
                if (el.ValueKind == JsonValueKind.String
                    && decimal.TryParse(el.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out dec)) { value = dec; return true; }
                return false;
            case ReportDataTypes.Boolean:
                if (el.ValueKind == JsonValueKind.True) { value = true; return true; }
                if (el.ValueKind == JsonValueKind.False) { value = false; return true; }
                if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var b)) { value = b; return true; }
                return false;
            case ReportDataTypes.Date:
                if (el.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(el.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                {
                    value = dt.Date;
                    return true;
                }
                return false;
            default:
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString() ?? "";
                    if (s.Length > 200) return false;
                    value = s;
                    return true;
                }
                if (el.ValueKind == JsonValueKind.Number) { value = el.GetRawText(); return true; }
                return false;
        }
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? "" : (value.Length <= max ? value : value[..max]);

    // -------------------------------------------------------------------------
    // Dataset definitions
    // -------------------------------------------------------------------------

    private const string Str = ReportDataTypes.String;
    private const string Num = ReportDataTypes.Number;
    private const string Dt = ReportDataTypes.Date;
    private const string Bool = ReportDataTypes.Boolean;

    private const string ConfidentialOffboarding = OffboardingPermissions.ViewConfidentialInformation;

    private static string Name(string a) =>
        $"COALESCE(NULLIF(LTRIM(RTRIM({a}.preferred_name)), N''), LTRIM(RTRIM({a}.first_name + N' ' + {a}.last_name)))";

    private static string Month(string expr) => $"CONVERT(varchar(7), {expr}, 120)";

    private const string OpenTaskStatuses = "N'Pending', N'InProgress', N'Blocked'";

    private static ReportFieldDef Dim(string key, string label, string sql, string type = Str, string? perm = null) =>
        new(key, label, type, sql, IsDimension: true, RequiredPermission: perm);

    private static ReportFieldDef Meas(string key, string label, string sql) =>
        new(key, label, Num, sql, IsMeasure: true);

    private static ReportFieldDef Plain(string key, string label, string sql, string type = Str, bool filterable = true, string? perm = null) =>
        new(key, label, type, sql, Filterable: filterable, RequiredPermission: perm);

    private static IReadOnlyList<ReportDatasetDef> BuildDatasets()
    {
        const string employeeJoins = @"
JOIN dbo.nh_hr_employees e ON e.id = {0}.employee_id AND e.organization_id = {0}.organization_id
LEFT JOIN dbo.nh_hr_departments d ON d.id = e.department_id AND d.organization_id = e.organization_id";

        var empName = Name("e");
        var list = new List<ReportDatasetDef>();

        // ---- employees ------------------------------------------------------
        list.Add(new ReportDatasetDef(
            "employees", "Employees", "Employee directory and workforce attributes (no personal contact or compensation data).",
            ReportsPermissions.ViewWorkforce,
            "e.organization_id",
            @"dbo.nh_hr_employees e
LEFT JOIN dbo.nh_hr_departments d ON d.id = e.department_id AND d.organization_id = e.organization_id
LEFT JOIN dbo.nh_hr_job_titles jt ON jt.id = e.job_title_id AND jt.organization_id = e.organization_id
LEFT JOIN dbo.nh_hr_work_locations wl ON wl.id = e.work_location_id AND wl.organization_id = e.organization_id
LEFT JOIN dbo.nh_hr_employment_types et ON et.id = e.employment_type_id AND et.organization_id = e.organization_id
LEFT JOIN dbo.nh_hr_employees m ON m.id = e.manager_id AND m.organization_id = e.organization_id",
            "e.archived_at IS NULL",
            [
                Plain("id", "Employee ID", "e.id", filterable: false),
                Plain("employee_number", "Employee number", "e.employee_number"),
                Dim("name", "Name", empName),
                Plain("work_email", "Work email", "e.work_email"),
                Dim("department", "Department", "COALESCE(d.name, N'Unassigned')"),
                Dim("job_title", "Job title", "COALESCE(jt.name, N'Unassigned')"),
                Dim("manager", "Manager", $"COALESCE({Name("m")}, N'None')"),
                Dim("location", "Work location", "COALESCE(wl.name, N'Unassigned')"),
                Dim("country", "Country", "COALESCE(wl.country, N'Unspecified')"),
                Dim("employment_type", "Employment type", "COALESCE(et.name, N'Unassigned')"),
                Dim("employment_status", "Employment status", "e.employment_status"),
                Dim("work_modality", "Work modality", "COALESCE(e.work_modality, N'Unspecified')"),
                Dim("hire_date", "Hire date", "e.hire_date", Dt),
                Dim("hire_month", "Hire month", Month("e.hire_date")),
                Dim("hire_year", "Hire year", "CAST(YEAR(e.hire_date) AS varchar(4))"),
                Dim("termination_date", "Termination date", "e.termination_date", Dt),
                Dim("termination_month", "Termination month", Month("e.termination_date")),
                Dim("tenure_bucket", "Tenure bucket",
                    @"CASE WHEN e.hire_date IS NULL THEN N'Unknown'
 WHEN DATEDIFF(day, e.hire_date, COALESCE(e.termination_date, @Today)) < 365 THEN N'< 1 year'
 WHEN DATEDIFF(day, e.hire_date, COALESCE(e.termination_date, @Today)) < 730 THEN N'1-2 years'
 WHEN DATEDIFF(day, e.hire_date, COALESCE(e.termination_date, @Today)) < 1825 THEN N'2-5 years'
 WHEN DATEDIFF(day, e.hire_date, COALESCE(e.termination_date, @Today)) < 3650 THEN N'5-10 years'
 ELSE N'10+ years' END"),
                new("tenure_years", "Tenure (years)", Num,
                    "CASE WHEN e.hire_date IS NULL THEN NULL ELSE CAST(DATEDIFF(day, e.hire_date, COALESCE(e.termination_date, @Today)) AS float) / 365.25 END",
                    IsMeasure: true)
            ],
            ["employee_number", "name", "job_title", "department", "manager", "location", "employment_type", "employment_status", "hire_date"],
            dateExpression: "COALESCE(e.hire_date, CAST(e.created_at AS date))",
            departmentExpression: "e.department_id"));

        // ---- departments ----------------------------------------------------
        list.Add(new ReportDatasetDef(
            "departments", "Departments", "Department structure with headcount and positions.",
            ReportsPermissions.ViewOrganization,
            "d.organization_id",
            @"dbo.nh_hr_departments d
LEFT JOIN dbo.nh_hr_departments pd ON pd.id = d.parent_department_id AND pd.organization_id = d.organization_id
LEFT JOIN dbo.nh_hr_employees h ON h.id = d.head_employee_id AND h.organization_id = d.organization_id
OUTER APPLY (SELECT COUNT(1) AS employee_count FROM dbo.nh_hr_employees x
             WHERE x.organization_id = d.organization_id AND x.department_id = d.id
               AND x.archived_at IS NULL AND x.employment_status <> N'Terminated') ec
OUTER APPLY (SELECT COUNT(1) AS position_count, ISNULL(SUM(p.approved_headcount), 0) AS approved_headcount
             FROM dbo.nh_hr_organizational_positions p
             WHERE p.organization_id = d.organization_id AND p.department_id = d.id
               AND p.archived_at IS NULL AND p.status <> N'Archived') pc",
            "d.archived_at IS NULL",
            [
                Plain("id", "Department ID", "d.id", filterable: false),
                Dim("name", "Department", "d.name"),
                Plain("code", "Code", "d.code"),
                Dim("parent_department", "Parent department", "COALESCE(pd.name, N'None')"),
                Dim("head", "Department head", $"COALESCE({Name("h")}, N'None')"),
                Plain("cost_center", "Cost center", "d.cost_center"),
                Dim("is_active", "Active", "d.is_active", Bool),
                Meas("employee_count", "Employees", "ec.employee_count"),
                Meas("position_count", "Positions", "pc.position_count"),
                Meas("approved_headcount", "Approved headcount", "pc.approved_headcount")
            ],
            ["name", "code", "parent_department", "head", "employee_count", "position_count", "approved_headcount"],
            departmentExpression: "d.id"));

        // ---- organizational_positions ---------------------------------------
        list.Add(new ReportDatasetDef(
            "organizational_positions", "Organizational positions", "Positions, approved headcount, occupancy and vacancies.",
            ReportsPermissions.ViewOrganization,
            "p.organization_id",
            @"dbo.nh_hr_organizational_positions p
JOIN dbo.nh_hr_departments d ON d.id = p.department_id AND d.organization_id = p.organization_id
LEFT JOIN dbo.nh_hr_job_titles jt ON jt.id = p.job_title_id AND jt.organization_id = p.organization_id
LEFT JOIN dbo.nh_hr_work_locations wl ON wl.id = p.work_location_id AND wl.organization_id = p.organization_id
LEFT JOIN dbo.nh_hr_organizational_levels ol ON ol.id = p.organizational_level_id AND ol.organization_id = p.organization_id
OUTER APPLY (SELECT COUNT(1) AS filled FROM dbo.nh_hr_employee_position_assignments a
             WHERE a.organization_id = p.organization_id AND a.position_id = p.id
               AND a.end_date IS NULL AND a.status = N'Active') fc",
            "p.archived_at IS NULL AND p.status <> N'Archived'",
            [
                Plain("id", "Position ID", "p.id", filterable: false),
                Dim("position_name", "Position", "p.position_name"),
                Plain("position_code", "Code", "p.position_code"),
                Dim("department", "Department", "d.name"),
                Dim("job_title", "Job title", "COALESCE(jt.name, N'Unassigned')"),
                Dim("level", "Level", "COALESCE(ol.name, N'Unassigned')"),
                Dim("location", "Work location", "COALESCE(wl.name, N'Unassigned')"),
                Dim("status", "Status", "p.status"),
                Plain("effective_date", "Effective date", "p.effective_date", Dt),
                Meas("approved_headcount", "Approved headcount", "p.approved_headcount"),
                Meas("filled_count", "Filled seats", "fc.filled"),
                Meas("vacancies", "Vacancies", "CASE WHEN p.approved_headcount > fc.filled THEN p.approved_headcount - fc.filled ELSE 0 END")
            ],
            ["position_name", "position_code", "department", "level", "status", "approved_headcount", "filled_count", "vacancies"],
            departmentExpression: "p.department_id"));

        // ---- work_locations -------------------------------------------------
        list.Add(new ReportDatasetDef(
            "work_locations", "Work locations", "Offices and remote locations with employee counts.",
            ReportsPermissions.ViewOrganization,
            "l.organization_id",
            @"dbo.nh_hr_work_locations l
OUTER APPLY (SELECT COUNT(1) AS employee_count FROM dbo.nh_hr_employees x
             WHERE x.organization_id = l.organization_id AND x.work_location_id = l.id
               AND x.archived_at IS NULL AND x.employment_status <> N'Terminated') ec",
            "l.archived_at IS NULL",
            [
                Plain("id", "Location ID", "l.id", filterable: false),
                Dim("name", "Location", "l.name"),
                Dim("city", "City", "COALESCE(l.city, N'Unspecified')"),
                Dim("state", "State", "COALESCE(l.state, N'Unspecified')"),
                Dim("country", "Country", "COALESCE(l.country, N'Unspecified')"),
                Dim("location_type", "Type", "COALESCE(l.location_type, N'Office')"),
                Dim("is_headquarters", "Headquarters", "l.is_headquarters", Bool),
                Dim("is_active", "Active", "l.is_active", Bool),
                Meas("employee_count", "Employees", "ec.employee_count")
            ],
            ["name", "city", "country", "location_type", "is_headquarters", "employee_count"]));

        // ---- onboardings ----------------------------------------------------
        list.Add(new ReportDatasetDef(
            "onboardings", "Onboardings", "Employee onboarding processes with task progress.",
            ReportsPermissions.ViewOnboarding,
            "o.organization_id",
            @"dbo.nh_hr_employee_onboardings o" + string.Format(employeeJoins, "o") + @"
LEFT JOIN dbo.nh_hr_onboarding_templates t ON t.id = o.template_id AND t.organization_id = o.organization_id
OUTER APPLY (SELECT COUNT(1) AS total_tasks,
                    ISNULL(SUM(CASE WHEN k.status = N'Completed' THEN 1 ELSE 0 END), 0) AS completed_tasks,
                    ISNULL(SUM(CASE WHEN k.due_date < @Today AND k.status IN (" + OpenTaskStatuses + @") THEN 1 ELSE 0 END), 0) AS overdue_tasks
             FROM dbo.nh_hr_onboarding_tasks k
             WHERE k.organization_id = o.organization_id AND k.onboarding_id = o.id AND k.status <> N'Cancelled') tk",
            null,
            [
                Plain("id", "Onboarding ID", "o.id", filterable: false),
                Dim("employee", "Employee", empName),
                Dim("department", "Department", "COALESCE(d.name, N'Unassigned')"),
                Dim("template", "Template", "COALESCE(t.name, N'None')"),
                Dim("status", "Status", "o.status"),
                Dim("start_date", "Start date", "o.start_date", Dt),
                Dim("start_month", "Start month", Month("o.start_date")),
                Plain("expected_completion_date", "Expected completion", "o.expected_completion_date", Dt),
                Plain("actual_completion_date", "Completed on", "o.actual_completion_date", Dt),
                Meas("total_tasks", "Total tasks", "tk.total_tasks"),
                Meas("completed_tasks", "Completed tasks", "tk.completed_tasks"),
                Meas("overdue_tasks", "Overdue tasks", "tk.overdue_tasks"),
                Meas("progress_pct", "Progress (%)", "CASE WHEN tk.total_tasks = 0 THEN 0 ELSE tk.completed_tasks * 100.0 / tk.total_tasks END"),
                Meas("completion_days", "Days to complete", "DATEDIFF(day, o.start_date, o.actual_completion_date)"),
                Meas("is_overdue", "Overdue (0/1)",
                    "CASE WHEN o.expected_completion_date < @Today AND o.status IN (N'NotStarted', N'InProgress', N'InReview') THEN 1 ELSE 0 END")
            ],
            ["employee", "department", "template", "status", "start_date", "expected_completion_date", "total_tasks", "completed_tasks", "progress_pct"],
            dateExpression: "COALESCE(o.start_date, CAST(o.created_at AS date))",
            departmentExpression: "e.department_id"));

        // ---- onboarding_tasks -----------------------------------------------
        list.Add(new ReportDatasetDef(
            "onboarding_tasks", "Onboarding tasks", "Individual onboarding tasks with status, due dates and completion times.",
            ReportsPermissions.ViewOnboarding,
            "k.organization_id",
            @"dbo.nh_hr_onboarding_tasks k
JOIN dbo.nh_hr_employee_onboardings o ON o.id = k.onboarding_id AND o.organization_id = k.organization_id
JOIN dbo.nh_hr_employees e ON e.id = o.employee_id AND e.organization_id = o.organization_id
LEFT JOIN dbo.nh_hr_departments d ON d.id = e.department_id AND d.organization_id = e.organization_id",
            null,
            [
                Plain("id", "Task ID", "k.id", filterable: false),
                Plain("title", "Task", "k.title"),
                Dim("category", "Category", "k.category"),
                Dim("status", "Status", "k.status"),
                Dim("assignment_type", "Assigned to", "k.assignment_type"),
                Dim("approval_status", "Approval", "COALESCE(k.approval_status, N'None')"),
                Dim("employee", "Employee", empName),
                Dim("department", "Department", "COALESCE(d.name, N'Unassigned')"),
                Dim("onboarding_status", "Onboarding status", "o.status"),
                Dim("due_date", "Due date", "k.due_date", Dt),
                Dim("due_month", "Due month", Month("k.due_date")),
                Plain("completed_date", "Completed on", "CAST(k.completed_at AS date)", Dt),
                Meas("is_overdue", "Overdue (0/1)", $"CASE WHEN k.due_date < @Today AND k.status IN ({OpenTaskStatuses}) THEN 1 ELSE 0 END"),
                Meas("days_to_complete", "Days to complete", "DATEDIFF(day, k.created_at, k.completed_at)")
            ],
            ["title", "category", "status", "employee", "department", "due_date", "completed_date"],
            dateExpression: "COALESCE(k.due_date, CAST(k.created_at AS date))",
            departmentExpression: "e.department_id"));

        // ---- offboardings ---------------------------------------------------
        list.Add(new ReportDatasetDef(
            "offboardings", "Offboardings", "Employee offboarding processes, clearance progress and separation types.",
            ReportsPermissions.ViewOffboarding,
            "f.organization_id",
            @"dbo.nh_hr_employee_offboardings f" + string.Format(employeeJoins, "f") + @"
OUTER APPLY (SELECT COUNT(1) AS total_tasks,
                    ISNULL(SUM(CASE WHEN k.status = N'Completed' THEN 1 ELSE 0 END), 0) AS completed_tasks,
                    ISNULL(SUM(CASE WHEN k.due_date < @Today AND k.status IN (" + OpenTaskStatuses + @") THEN 1 ELSE 0 END), 0) AS overdue_tasks
             FROM dbo.nh_hr_offboarding_tasks k
             WHERE k.organization_id = f.organization_id AND k.offboarding_id = f.id AND k.status <> N'Cancelled') tk
OUTER APPLY (SELECT COUNT(1) AS pending_assets FROM dbo.nh_hr_offboarding_assets s
             WHERE s.organization_id = f.organization_id AND s.offboarding_id = f.id AND s.status = N'PendingReturn') pa
OUTER APPLY (SELECT COUNT(1) AS pending_access FROM dbo.nh_hr_offboarding_access_items s
             WHERE s.organization_id = f.organization_id AND s.offboarding_id = f.id AND s.status IN (N'Pending', N'Scheduled')) pc",
            null,
            [
                Plain("id", "Offboarding ID", "f.id", filterable: false),
                Dim("employee", "Employee", empName),
                Dim("department", "Department", "COALESCE(d.name, N'Unassigned')"),
                Dim("separation_type", "Separation type", "f.separation_type"),
                Dim("status", "Status", "f.status"),
                Dim("separation_reason", "Separation reason (confidential)", "COALESCE(NULLIF(LTRIM(RTRIM(f.separation_reason)), N''), N'Not specified')", Str, ConfidentialOffboarding),
                Dim("rehire_eligibility", "Rehire eligibility (confidential)", "COALESCE(f.rehire_eligibility, N'Unknown')", Str, ConfidentialOffboarding),
                Plain("notice_date", "Notice date", "f.notice_date", Dt),
                Dim("last_working_day", "Last working day", "f.last_working_day", Dt),
                Dim("last_working_month", "Last working month", Month("f.last_working_day")),
                Plain("termination_effective_date", "Termination effective", "f.termination_effective_date", Dt),
                Plain("actual_completion_date", "Completed on", "f.actual_completion_date", Dt),
                Meas("total_tasks", "Total tasks", "tk.total_tasks"),
                Meas("completed_tasks", "Completed tasks", "tk.completed_tasks"),
                Meas("overdue_tasks", "Overdue tasks", "tk.overdue_tasks"),
                Meas("progress_pct", "Progress (%)", "CASE WHEN tk.total_tasks = 0 THEN 0 ELSE tk.completed_tasks * 100.0 / tk.total_tasks END"),
                Meas("pending_assets", "Pending asset returns", "pa.pending_assets"),
                Meas("pending_access", "Pending access revocations", "pc.pending_access"),
                Meas("completion_days", "Days to complete",
                    "DATEDIFF(day, COALESCE(f.process_start_date, CAST(f.created_at AS date)), f.actual_completion_date)"),
                Meas("notice_period_days", "Notice period (days)", "DATEDIFF(day, f.notice_date, f.last_working_day)")
            ],
            ["employee", "department", "separation_type", "status", "last_working_day", "total_tasks", "completed_tasks", "pending_assets", "pending_access"],
            dateExpression: "COALESCE(f.last_working_day, f.process_start_date, CAST(f.created_at AS date))",
            departmentExpression: "e.department_id"));

        // ---- offboarding_tasks ----------------------------------------------
        list.Add(new ReportDatasetDef(
            "offboarding_tasks", "Offboarding tasks", "Individual offboarding / clearance tasks.",
            ReportsPermissions.ViewOffboarding,
            "k.organization_id",
            @"dbo.nh_hr_offboarding_tasks k
JOIN dbo.nh_hr_employee_offboardings f ON f.id = k.offboarding_id AND f.organization_id = k.organization_id
JOIN dbo.nh_hr_employees e ON e.id = f.employee_id AND e.organization_id = f.organization_id
LEFT JOIN dbo.nh_hr_departments d ON d.id = e.department_id AND d.organization_id = e.organization_id",
            null,
            [
                Plain("id", "Task ID", "k.id", filterable: false),
                Plain("title", "Task", "k.title"),
                Dim("category", "Category", "k.category"),
                Dim("status", "Status", "k.status"),
                Dim("assignment_type", "Assigned to", "k.assignment_type"),
                Dim("approval_status", "Approval", "COALESCE(k.approval_status, N'None')"),
                Dim("employee", "Employee", empName),
                Dim("department", "Department", "COALESCE(d.name, N'Unassigned')"),
                Dim("offboarding_status", "Offboarding status", "f.status"),
                Dim("due_date", "Due date", "k.due_date", Dt),
                Plain("completed_date", "Completed on", "CAST(k.completed_at AS date)", Dt),
                Meas("is_overdue", "Overdue (0/1)", $"CASE WHEN k.due_date < @Today AND k.status IN ({OpenTaskStatuses}) THEN 1 ELSE 0 END"),
                Meas("days_to_complete", "Days to complete", "DATEDIFF(day, k.created_at, k.completed_at)")
            ],
            ["title", "category", "status", "employee", "department", "due_date", "completed_date"],
            dateExpression: "COALESCE(k.due_date, CAST(k.created_at AS date))",
            departmentExpression: "e.department_id"));

        // ---- employee_documents ---------------------------------------------
        list.Add(new ReportDatasetDef(
            "employee_documents", "Employee documents", "Document library metadata (never file contents) with expiry and compliance status.",
            ReportsPermissions.ViewCompliance,
            "x.organization_id",
            @"dbo.nh_hr_employee_documents x" + string.Format(employeeJoins, "x"),
            "x.archived_at IS NULL AND e.archived_at IS NULL",
            [
                Plain("id", "Document ID", "x.id", filterable: false),
                Plain("name", "Document", "x.name"),
                Dim("category", "Category", "x.category"),
                Dim("status", "Stored status", "x.status"),
                Dim("display_status", "Compliance status",
                    @"CASE WHEN x.status = N'Pending' THEN N'Pending'
 WHEN x.status = N'Expired' OR x.expires_at < @Today THEN N'Expired'
 WHEN x.expires_at <= DATEADD(day, 30, @Today) THEN N'Expiring'
 ELSE N'Valid' END"),
                Dim("employee", "Employee", empName),
                Dim("department", "Department", "COALESCE(d.name, N'Unassigned')"),
                Dim("expires_at", "Expires on", "x.expires_at", Dt),
                Dim("expiry_month", "Expiry month", Month("x.expires_at")),
                Plain("created_date", "Added on", "CAST(x.created_at AS date)", Dt),
                Meas("days_to_expiry", "Days to expiry", "DATEDIFF(day, @Today, x.expires_at)"),
                Meas("file_size_bytes", "File size (bytes)", "x.file_size_bytes")
            ],
            ["name", "category", "display_status", "employee", "department", "expires_at", "created_date"],
            dateExpression: "CAST(x.created_at AS date)",
            departmentExpression: "e.department_id"));

        // ---- document_requests ----------------------------------------------
        list.Add(new ReportDatasetDef(
            "document_requests", "Document requests", "Outstanding and fulfilled employee document requests.",
            ReportsPermissions.ViewCompliance,
            "r.organization_id",
            @"dbo.nh_hr_document_requests r" + string.Format(employeeJoins, "r"),
            "e.archived_at IS NULL",
            [
                Plain("id", "Request ID", "r.id", filterable: false),
                Plain("title", "Request", "r.title"),
                Dim("category", "Category", "r.category"),
                Dim("status", "Status", "CASE WHEN r.status = N'Open' AND r.due_at < @Today THEN N'Overdue' ELSE r.status END"),
                Dim("employee", "Employee", empName),
                Dim("department", "Department", "COALESCE(d.name, N'Unassigned')"),
                Dim("due_at", "Due date", "r.due_at", Dt),
                Plain("created_date", "Requested on", "CAST(r.created_at AS date)", Dt),
                Plain("completed_date", "Completed on", "CAST(r.completed_at AS date)", Dt),
                Meas("days_open", "Days open", "DATEDIFF(day, CAST(r.created_at AS date), COALESCE(CAST(r.completed_at AS date), @Today))"),
                Meas("days_overdue", "Days overdue", "CASE WHEN r.status = N'Open' AND r.due_at < @Today THEN DATEDIFF(day, r.due_at, @Today) ELSE 0 END")
            ],
            ["title", "category", "status", "employee", "department", "due_at", "created_date"],
            dateExpression: "CAST(r.created_at AS date)",
            departmentExpression: "e.department_id"));

        // ---- compensation ---------------------------------------------------
        list.Add(new ReportDatasetDef(
            "compensation", "Compensation", "Current compensation per active employee (annualized estimate). Aggregates require at least 5 employees per group.",
            ReportsPermissions.ViewCompensation,
            "c.organization_id",
            @"dbo.nh_hr_employee_compensation c
JOIN dbo.nh_hr_employees e ON e.id = c.employee_id AND e.organization_id = c.organization_id
LEFT JOIN dbo.nh_hr_departments d ON d.id = e.department_id AND d.organization_id = e.organization_id
LEFT JOIN dbo.nh_hr_job_titles jt ON jt.id = e.job_title_id AND jt.organization_id = e.organization_id
LEFT JOIN dbo.nh_hr_employment_types et ON et.id = e.employment_type_id AND et.organization_id = e.organization_id",
            @"e.archived_at IS NULL AND e.employment_status <> N'Terminated'
AND c.effective_date <= @Today AND (c.end_date IS NULL OR c.end_date >= @Today)
AND NOT EXISTS (SELECT 1 FROM dbo.nh_hr_employee_compensation c2
                WHERE c2.organization_id = c.organization_id AND c2.employee_id = c.employee_id
                  AND c2.effective_date <= @Today AND c2.effective_date > c.effective_date)",
            [
                Plain("id", "Compensation ID", "c.id", filterable: false),
                Dim("employee", "Employee", empName),
                Dim("department", "Department", "COALESCE(d.name, N'Unassigned')"),
                Dim("job_title", "Job title", "COALESCE(jt.name, N'Unassigned')"),
                Dim("employment_type", "Employment type", "COALESCE(et.name, N'Unassigned')"),
                Dim("currency", "Currency", "c.currency"),
                Dim("pay_frequency", "Pay frequency", "c.pay_frequency"),
                Plain("effective_date", "Effective date", "c.effective_date", Dt),
                Meas("amount", "Amount (per pay period)", "c.amount"),
                Meas("annualized_amount", "Annualized amount",
                    @"c.amount * CASE c.pay_frequency WHEN N'Hourly' THEN 2080 WHEN N'Weekly' THEN 52 WHEN N'BiWeekly' THEN 26
 WHEN N'SemiMonthly' THEN 24 WHEN N'Monthly' THEN 12 ELSE 1 END")
            ],
            ["employee", "department", "job_title", "currency", "pay_frequency", "amount", "annualized_amount"],
            dateExpression: "c.effective_date",
            departmentExpression: "e.department_id",
            minCohortSize: CompensationMinCohort));

        return list;
    }

    /// <summary>Escapes <c>%</c>, <c>_</c>, <c>[</c> and the escape character for use inside a LIKE pattern (ESCAPE '\').</summary>
    public static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
}
