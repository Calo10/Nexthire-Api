namespace nexthire_api.DTOs;

public class JobDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Department { get; set; }
    public string? Location { get; set; }
    public string? Description { get; set; }
    public string Status { get; set; } = string.Empty;
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
}

public class UpdateJobDto
{
    public string? Title { get; set; }
    public string? Department { get; set; }
    public string? Location { get; set; }
    public string? Description { get; set; }
    public string? Status { get; set; }
}

