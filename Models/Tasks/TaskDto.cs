namespace nexthire_api.Models.Tasks;

public class TaskDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset? DueAt { get; set; }
    public Guid ApplicationId { get; set; }
    public Guid JobId { get; set; }
    public Guid CandidateId { get; set; }
    public Guid? AssignedToUserId { get; set; }
    public string? JobTitle { get; set; }
    public string? CandidateName { get; set; }
    public string? AssignedToName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

