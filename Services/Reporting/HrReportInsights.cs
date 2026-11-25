using System.Globalization;
using nexthire_api.DTOs.CoreHr;

namespace nexthire_api.Services.Reporting;

/// <summary>
/// Deterministic, rule-based insights derived from overview metrics. No AI/LLM and no randomness: the same inputs always
/// produce the same insights in the same order.
/// </summary>
public static class HrReportInsights
{
    public const int MaxInsights = 6;

    public static IReadOnlyList<InsightDto> Build(HrReportsOverviewDto o)
    {
        var list = new List<InsightDto>();

        // Headcount growth
        var growth = o.TotalEmployees.DeltaPct;
        if (growth is { } g && o.TotalEmployees.PreviousValue > 0)
        {
            if (g >= 1)
                list.Add(Insight("HEADCOUNT_GROWTH", "Positive", "Headcount is growing",
                    $"Headcount grew {Pct(g)} compared to the previous period ({Num(o.TotalEmployees.PreviousValue)} to {Num(o.TotalEmployees.Value)} employees)."));
            else if (g <= -1)
                list.Add(Insight("HEADCOUNT_DECLINE", "Warning", "Headcount is shrinking",
                    $"Headcount fell {Pct(Math.Abs(g))} compared to the previous period ({Num(o.TotalEmployees.PreviousValue)} to {Num(o.TotalEmployees.Value)} employees)."));
        }

        // Turnover change
        if (o.TurnoverRate.PreviousValue is { } prevTurnover && o.TurnoverRate.Delta is { } d)
        {
            if (d >= 1)
                list.Add(Insight("TURNOVER_UP", "Warning", "Turnover increased",
                    $"Turnover rose to {o.TurnoverRate.Value.ToString("0.#", CultureInfo.InvariantCulture)}% from {prevTurnover.ToString("0.#", CultureInfo.InvariantCulture)}% in the previous period."));
            else if (d <= -1)
                list.Add(Insight("TURNOVER_DOWN", "Positive", "Turnover decreased",
                    $"Turnover dropped to {o.TurnoverRate.Value.ToString("0.#", CultureInfo.InvariantCulture)}% from {prevTurnover.ToString("0.#", CultureInfo.InvariantCulture)}% in the previous period."));
        }

        // Largest department
        var top = o.WorkforceByDepartment.OrderByDescending(x => x.Value).ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (top is { Value: > 0 } && o.WorkforceByDepartment.Count > 1)
        {
            list.Add(Insight("TOP_DEPARTMENT", "Info", "Largest department",
                $"{top.Label} is the largest department with {Num(top.Value)} employees" +
                (top.Percent is { } p ? $" ({Pct(p)} of the workforce)." : ".")));
        }

        // Onboarding time
        var ob = o.Onboarding;
        if (ob.PreviousAverageCompletionDays is > 0 && ob.AverageCompletionDays > 0)
        {
            if (ob.CompletionDaysDeltaPct <= -10)
                list.Add(Insight("ONBOARDING_FASTER", "Positive", "Onboarding is getting faster",
                    $"Average onboarding time improved {Pct(Math.Abs(ob.CompletionDaysDeltaPct))} to {ob.AverageCompletionDays.ToString("0.#", CultureInfo.InvariantCulture)} days."));
            else if (ob.CompletionDaysDeltaPct >= 10)
                list.Add(Insight("ONBOARDING_SLOWER", "Warning", "Onboarding is taking longer",
                    $"Average onboarding time increased {Pct(ob.CompletionDaysDeltaPct)} to {ob.AverageCompletionDays.ToString("0.#", CultureInfo.InvariantCulture)} days."));
        }

        // Offboarding time
        var off = o.Offboarding;
        if (off.PreviousAverageCompletionDays is > 0 && off.AverageCompletionDays > 0 && off.CompletionDaysDeltaPct >= 10)
        {
            list.Add(Insight("OFFBOARDING_SLOWER", "Warning", "Offboarding is taking longer",
                $"Average offboarding time increased {Pct(off.CompletionDaysDeltaPct)} to {off.AverageCompletionDays.ToString("0.#", CultureInfo.InvariantCulture)} days."));
        }

        // Documents compliance
        var docs = o.Documents;
        if (docs.TotalDocuments + docs.OpenRequests > 0)
        {
            if (docs.CompletenessPct >= 90)
                list.Add(Insight("DOCS_COMPLIANT", "Positive", "Document compliance is strong",
                    $"{Pct(docs.CompletenessPct)} of required employee documents are on file and valid."));
            else if (docs.CompletenessPct < 70)
                list.Add(Insight("DOCS_LOW_COMPLIANCE", "Warning", "Document compliance needs attention",
                    $"Only {Pct(docs.CompletenessPct)} of required employee documents are on file and valid."));
        }
        if (docs.Expired > 0)
            list.Add(Insight("DOCS_EXPIRED", "Warning", "Expired documents",
                $"{Num(docs.Expired)} employee document(s) have expired."));
        if (docs.ExpiringIn30Days > 0)
            list.Add(Insight("DOCS_EXPIRING", "Warning", "Documents expiring soon",
                $"{Num(docs.ExpiringIn30Days)} employee document(s) expire within 30 days."));
        if (docs.OpenRequests > 0)
            list.Add(Insight("DOCS_OPEN_REQUESTS", "Info", "Open document requests",
                $"{Num(docs.OpenRequests)} document request(s) are still open."));

        // Hiring
        if (o.NewHires.PreviousValue is { } prevHires && o.NewHires.Value > prevHires && o.NewHires.Value >= 3)
            list.Add(Insight("HIRING_UP", "Positive", "Hiring picked up",
                $"{Num(o.NewHires.Value)} new hires this period versus {Num(prevHires)} in the previous period."));

        return list.Take(MaxInsights).ToList();
    }

    private static InsightDto Insight(string code, string severity, string title, string message) =>
        new() { Code = code, Severity = severity, Title = title, Message = message };

    private static string Pct(double value) => value.ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static string Num(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Num(double? value) => value.HasValue ? Num(value.Value) : "n/a";
}
