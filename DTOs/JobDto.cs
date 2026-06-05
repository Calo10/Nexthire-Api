namespace nexthire_api.DTOs;

public static class JobLanguageCodes
{
    public const string Spanish = "es";
    public const string English = "en";

    public static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        Spanish,
        English
    };

    public static string NormalizeOrDefault(string? value) =>
        string.Equals(value, English, StringComparison.OrdinalIgnoreCase) ? English : Spanish;
}

public class JobBotContextDto
{
    public Guid OrgId { get; set; }
    public string Language { get; set; } = JobLanguageCodes.Spanish;
}

public class JobDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Department { get; set; }
    public string? Location { get; set; }
    public string? Description { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Language { get; set; } = JobLanguageCodes.Spanish;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>Distinct candidates with an application for this job.</summary>
    public int ApplicantsCount { get; set; }
}

public class CreateJobDto
{
    public string Title { get; set; } = string.Empty;
    public string? Department { get; set; }
    public string? Location { get; set; }
    public string? Description { get; set; }
    public string? Status { get; set; }
    public string? Language { get; set; }
}

public class UpdateJobDto
{
    public string? Title { get; set; }
    public string? Department { get; set; }
    public string? Location { get; set; }
    public string? Description { get; set; }
    public string? Status { get; set; }
    public string? Language { get; set; }
}

