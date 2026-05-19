namespace nexthire_api.DTOs;

public class DashboardSummaryDto
{
    public int OpenJobs { get; set; }
    public int NewCandidates { get; set; }
    public int NewApplications { get; set; }
    public int UpcomingInterviews { get; set; }
    public int OffersSent { get; set; }
    public decimal? AvgTimeToHireDays { get; set; }
}

public class ApplicationsByStageDto
{
    public Guid StageId { get; set; }
    public string StageName { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class ActivityTrendDto
{
    public DateTime Date { get; set; }
    public int Applications { get; set; }
    public int Candidates { get; set; }
}

public class MyJobDto
{
    public Guid JobId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public int ApplicationsCount { get; set; }
    public DateTimeOffset? LastActivityAt { get; set; }
}

public class RecentCandidateDto
{
    public Guid CandidateId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? CurrentStageName { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class UpcomingTaskDto
{
    public Guid TaskId { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTimeOffset? DueAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public Guid? RelatedJobId { get; set; }
    public Guid? RelatedCandidateId { get; set; }
}
