using System.Globalization;
using System.Text.Json;

namespace nexthire_api.Services.Reporting;

/// <summary>Computes next-run instants for report schedules (Daily / Weekly / Monthly / Quarterly).</summary>
public static class ReportScheduleCalculator
{
    public static readonly IReadOnlyList<string> Frequencies = ["Daily", "Weekly", "Monthly", "Quarterly"];

    /// <summary>Canonical frequency name or null when unknown.</summary>
    public static string? NormalizeFrequency(string? value) =>
        Frequencies.FirstOrDefault(f => f.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    public static bool IsKnownTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>
    /// Schedule JSON: <c>{"time":"08:00","dayOfWeek":1,"dayOfMonth":1}</c> (all optional; dayOfWeek 0=Sunday).
    /// Returns an error message when the JSON is invalid.
    /// </summary>
    public static string? ValidateScheduleJson(string? scheduleJson)
    {
        if (string.IsNullOrWhiteSpace(scheduleJson)) return null;
        return TryParse(scheduleJson, out _, out _, out _, out var error) ? null : error;
    }

    public static DateTimeOffset ComputeNextRun(string frequency, string? scheduleJson, string? timezone, DateTimeOffset afterUtc)
    {
        TryParse(scheduleJson, out var time, out var dayOfWeek, out var dayOfMonth, out _);

        var tz = ResolveTimeZone(timezone);
        var local = TimeZoneInfo.ConvertTime(afterUtc, tz).DateTime;
        var date = local.Date;

        DateTime candidate;
        switch (NormalizeFrequency(frequency))
        {
            case "Daily":
                candidate = date + time;
                if (candidate <= local) candidate = candidate.AddDays(1);
                break;

            case "Weekly":
                candidate = date + time;
                var diff = ((int)dayOfWeek - (int)candidate.DayOfWeek + 7) % 7;
                candidate = candidate.AddDays(diff);
                if (candidate <= local) candidate = candidate.AddDays(7);
                break;

            case "Monthly":
                candidate = MonthlyCandidate(date.Year, date.Month, dayOfMonth, time);
                if (candidate <= local)
                {
                    var next = date.AddMonths(1);
                    candidate = MonthlyCandidate(next.Year, next.Month, dayOfMonth, time);
                }
                break;

            case "Quarterly":
                var quarterStartMonth = ((date.Month - 1) / 3) * 3 + 1;
                candidate = MonthlyCandidate(date.Year, quarterStartMonth, dayOfMonth, time);
                if (candidate <= local)
                {
                    var next = new DateTime(date.Year, quarterStartMonth, 1).AddMonths(3);
                    candidate = MonthlyCandidate(next.Year, next.Month, dayOfMonth, time);
                }
                break;

            default:
                candidate = date.AddDays(1) + time;
                break;
        }

        return ToUtc(candidate, tz);
    }

    private static DateTime MonthlyCandidate(int year, int month, int dayOfMonth, TimeSpan time)
    {
        var day = Math.Min(dayOfMonth, DateTime.DaysInMonth(year, month));
        return new DateTime(year, month, day) + time;
    }

    private static DateTimeOffset ToUtc(DateTime localUnspecified, TimeZoneInfo tz)
    {
        var unspecified = DateTime.SpecifyKind(localUnspecified, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(unspecified)) unspecified = unspecified.AddHours(1);
        var utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, tz);
        return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private static bool TryParse(string? json, out TimeSpan time, out DayOfWeek dayOfWeek, out int dayOfMonth, out string? error)
    {
        time = new TimeSpan(8, 0, 0);
        dayOfWeek = DayOfWeek.Monday;
        dayOfMonth = 1;
        error = null;
        if (string.IsNullOrWhiteSpace(json)) return true;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Schedule must be a JSON object.";
                return false;
            }

            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name.Equals("time", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                {
                    if (!TimeSpan.TryParseExact(p.Value.GetString(), @"hh\:mm", CultureInfo.InvariantCulture, out time))
                    {
                        error = "Schedule time must use HH:mm.";
                        return false;
                    }
                }
                else if (p.Name.Equals("dayOfWeek", StringComparison.OrdinalIgnoreCase))
                {
                    if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var n) && n is >= 0 and <= 6)
                        dayOfWeek = (DayOfWeek)n;
                    else if (p.Value.ValueKind == JsonValueKind.String && Enum.TryParse<DayOfWeek>(p.Value.GetString(), true, out var named))
                        dayOfWeek = named;
                    else
                    {
                        error = "dayOfWeek must be 0-6 or a weekday name.";
                        return false;
                    }
                }
                else if (p.Name.Equals("dayOfMonth", StringComparison.OrdinalIgnoreCase))
                {
                    if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var d) && d is >= 1 and <= 31)
                        dayOfMonth = d;
                    else
                    {
                        error = "dayOfMonth must be between 1 and 31.";
                        return false;
                    }
                }
            }
            return true;
        }
        catch (JsonException)
        {
            error = "Schedule must be valid JSON.";
            return false;
        }
    }
}
