using System.Text;

namespace nexthire_api.Services.Reporting;

/// <summary>Per-request values bound as SQL parameters.</summary>
/// <param name="OrganizationId">Tenant; always ANDed into the query.</param>
/// <param name="Today">UTC date used by date-relative expressions (<c>@Today</c>).</param>
/// <param name="From">Optional lower bound on the dataset date expression.</param>
/// <param name="To">Optional upper bound on the dataset date expression.</param>
/// <param name="DepartmentId">Optional department scope (ignored by datasets without a department expression).</param>
/// <param name="MaxRowsOverride">Row cap replacing the configuration limit (exports).</param>
public sealed record ReportQueryContext(
    Guid OrganizationId,
    DateTime Today,
    DateTime? From = null,
    DateTime? To = null,
    Guid? DepartmentId = null,
    int? MaxRowsOverride = null);

/// <summary>An output column; <see cref="SqlAlias"/> is a generated alias (<c>c0</c>, <c>d1</c>, <c>m0</c>), never user text.</summary>
public sealed record ReportQueryColumn(string SqlAlias, string Key, string Label, string DataType);

public sealed record BuiltReportQuery(
    string Sql,
    IReadOnlyDictionary<string, object?> Parameters,
    IReadOnlyList<ReportQueryColumn> Columns,
    int RowLimit);

/// <summary>
/// Builds parameterized SQL from a validated <see cref="ReportConfiguration"/>. Only allowlisted SQL expressions from
/// <see cref="ReportDatasetRegistry"/> are ever placed in the statement; every user-provided value is a parameter and
/// output aliases are generated. The tenant predicate is always applied.
/// </summary>
public static class ReportQueryBuilder
{
    public static BuiltReportQuery Build(ReportDatasetDef dataset, ReportConfiguration config, ReportQueryContext context)
    {
        var rowLimit = Math.Clamp(context.MaxRowsOverride ?? config.Limit, 1, 10_000);

        var parameters = new Dictionary<string, object?>
        {
            ["OrganizationId"] = context.OrganizationId,
            ["Today"] = context.Today.Date,
            // One extra row lets the caller detect truncation.
            ["Limit"] = rowLimit + 1
        };

        var columns = new List<ReportQueryColumn>();
        var select = new List<string>();
        var groupBy = new List<string>();
        // output key -> SQL alias (used for ORDER BY)
        var aliasByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (config.IsAggregated)
        {
            var i = 0;
            foreach (var key in config.Dimensions)
            {
                var field = dataset.GetField(key) ?? throw new InvalidOperationException($"Unknown field '{key}'.");
                var alias = $"d{i++}";
                select.Add($"{field.SqlExpression} AS {alias}");
                groupBy.Add(field.SqlExpression);
                columns.Add(new ReportQueryColumn(alias, field.Key, field.Label, field.DataType));
                aliasByKey[field.Key] = alias;
            }

            i = 0;
            foreach (var measure in config.Measures)
            {
                var field = dataset.GetField(measure.Field) ?? throw new InvalidOperationException($"Unknown field '{measure.Field}'.");
                var alias = $"m{i++}";
                select.Add($"{AggregateSql(measure.Agg, field.SqlExpression)} AS {alias}");
                columns.Add(new ReportQueryColumn(alias, measure.OutputKey, $"{AggregateLabel(measure.Agg)} of {field.Label}",
                    measure.Agg is ReportAggregations.Min or ReportAggregations.Max && field.DataType == ReportDataTypes.Date
                        ? ReportDataTypes.Date
                        : ReportDataTypes.Number));
                aliasByKey[measure.OutputKey] = alias;
            }
        }
        else
        {
            var i = 0;
            foreach (var key in config.Columns)
            {
                var field = dataset.GetField(key) ?? throw new InvalidOperationException($"Unknown field '{key}'.");
                var alias = $"c{i++}";
                select.Add($"{field.SqlExpression} AS {alias}");
                columns.Add(new ReportQueryColumn(alias, field.Key, field.Label, field.DataType));
                aliasByKey[field.Key] = alias;
            }
        }

        if (select.Count == 0)
            throw new InvalidOperationException("Report has no columns.");

        // WHERE: tenant first, then dataset base predicate, global filters, user filters.
        var where = new StringBuilder();
        where.Append($"{dataset.OrganizationColumn} = @OrganizationId");
        if (!string.IsNullOrWhiteSpace(dataset.BaseWhereSql))
            where.Append($"\n  AND ({dataset.BaseWhereSql})");

        if (dataset.DateExpression != null)
        {
            if (context.From.HasValue)
            {
                where.Append($"\n  AND {dataset.DateExpression} >= @From");
                parameters["From"] = context.From.Value.Date;
            }
            if (context.To.HasValue)
            {
                where.Append($"\n  AND {dataset.DateExpression} <= @To");
                parameters["To"] = context.To.Value.Date;
            }
        }

        if (dataset.DepartmentExpression != null && context.DepartmentId.HasValue)
        {
            where.Append($"\n  AND {dataset.DepartmentExpression} = @DepartmentId");
            parameters["DepartmentId"] = context.DepartmentId.Value;
        }

        for (var f = 0; f < config.Filters.Count; f++)
        {
            var filter = config.Filters[f];
            var field = dataset.GetField(filter.Field) ?? throw new InvalidOperationException($"Unknown field '{filter.Field}'.");
            where.Append("\n  AND ").Append(FilterSql(field.SqlExpression, filter, $"f{f}", parameters));
        }

        var sql = new StringBuilder();
        sql.Append("SELECT TOP (@Limit) ").Append(string.Join(", ", select));
        sql.Append("\nFROM ").Append(dataset.FromSql);
        sql.Append("\nWHERE ").Append(where);

        if (config.IsAggregated)
        {
            if (groupBy.Count > 0)
                sql.Append("\nGROUP BY ").Append(string.Join(", ", groupBy));

            if (dataset.MinCohortSize > 0)
            {
                // Privacy threshold: aggregated rows must be backed by at least N records.
                sql.Append("\nHAVING COUNT(1) >= @MinCohort");
                parameters["MinCohort"] = dataset.MinCohortSize;
            }
        }

        // ORDER BY uses generated aliases only.
        var order = new List<string>();
        foreach (var s in config.Sort)
        {
            if (aliasByKey.TryGetValue(s.Field, out var alias))
                order.Add($"{alias} {(s.Descending ? "DESC" : "ASC")}");
        }
        if (order.Count == 0)
        {
            if (config.IsAggregated)
            {
                if (config.Measures.Count > 0) order.Add("m0 DESC");
                if (config.Dimensions.Count > 0) order.Add("d0 ASC");
            }
            else
            {
                order.Add("c0 ASC");
            }
        }
        if (order.Count > 0)
            sql.Append("\nORDER BY ").Append(string.Join(", ", order));

        return new BuiltReportQuery(sql.ToString(), parameters, columns, rowLimit);
    }

    private static string AggregateSql(string agg, string expr) => agg switch
    {
        ReportAggregations.Count => $"COUNT({expr})",
        ReportAggregations.DistinctCount => $"COUNT(DISTINCT {expr})",
        ReportAggregations.Sum => $"SUM(CAST({expr} AS decimal(38, 4)))",
        ReportAggregations.Avg => $"AVG(CAST({expr} AS float))",
        ReportAggregations.Min => $"MIN({expr})",
        ReportAggregations.Max => $"MAX({expr})",
        _ => throw new InvalidOperationException($"Unsupported aggregation '{agg}'.")
    };

    private static string AggregateLabel(string agg) => agg switch
    {
        ReportAggregations.Count => "Count",
        ReportAggregations.DistinctCount => "Distinct count",
        ReportAggregations.Sum => "Sum",
        ReportAggregations.Avg => "Average",
        ReportAggregations.Min => "Minimum",
        ReportAggregations.Max => "Maximum",
        _ => agg
    };

    private static string FilterSql(string expr, ReportFilter filter, string prefix, Dictionary<string, object?> parameters)
    {
        switch (filter.Operator)
        {
            case ReportFilterOperators.IsNull:
                return $"{expr} IS NULL";
            case ReportFilterOperators.IsNotNull:
                return $"{expr} IS NOT NULL";
            case ReportFilterOperators.In:
            {
                var names = new List<string>();
                for (var i = 0; i < filter.Values.Count; i++)
                {
                    var name = $"{prefix}_{i}";
                    parameters[name] = filter.Values[i];
                    names.Add("@" + name);
                }
                return $"{expr} IN ({string.Join(", ", names)})";
            }
            case ReportFilterOperators.Contains:
                parameters[prefix] = "%" + ReportDatasetRegistry.EscapeLike(Convert.ToString(filter.Values[0]) ?? "") + "%";
                return $"{expr} LIKE @{prefix} ESCAPE '\\'";
            case ReportFilterOperators.StartsWith:
                parameters[prefix] = ReportDatasetRegistry.EscapeLike(Convert.ToString(filter.Values[0]) ?? "") + "%";
                return $"{expr} LIKE @{prefix} ESCAPE '\\'";
            case ReportFilterOperators.Neq:
                parameters[prefix] = filter.Values[0];
                return $"({expr} <> @{prefix} OR {expr} IS NULL)";
        }

        var op = filter.Operator switch
        {
            ReportFilterOperators.Eq => "=",
            ReportFilterOperators.Gt => ">",
            ReportFilterOperators.Gte => ">=",
            ReportFilterOperators.Lt => "<",
            ReportFilterOperators.Lte => "<=",
            _ => throw new InvalidOperationException($"Unsupported operator '{filter.Operator}'.")
        };
        parameters[prefix] = filter.Values[0];
        return $"{expr} {op} @{prefix}";
    }
}
