namespace nexthire_api.DTOs;

/// <summary>
/// Public job detail for apply flows (web + shared shape with WhatsApp bot questions).
/// </summary>
public class PublicJobDto
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
    public int ApplicantsCount { get; set; }

    /// <summary>Active custom questions (same set used by the WhatsApp apply bot).</summary>
    public IReadOnlyList<JobBotQuestionDto> BotQuestions { get; set; } = Array.Empty<JobBotQuestionDto>();

    public static PublicJobDto FromJob(JobDto job, IReadOnlyList<JobBotQuestionDto> botQuestions) =>
        new()
        {
            Id = job.Id,
            Title = job.Title,
            Department = job.Department,
            Location = job.Location,
            Description = job.Description,
            Status = job.Status,
            Language = job.Language,
            CreatedAt = job.CreatedAt,
            UpdatedAt = job.UpdatedAt,
            ApplicantsCount = job.ApplicantsCount,
            BotQuestions = botQuestions
                .Where(q => q.IsActive)
                .OrderBy(q => q.SortOrder)
                .ThenBy(q => q.CreatedAt)
                .ThenBy(q => q.Id)
                .ToList()
        };
}
