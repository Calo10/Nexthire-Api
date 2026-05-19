namespace nexthire_api.Models.Notes;

public class NoteDto
{
    public Guid Id { get; set; }
    public Guid ApplicationId { get; set; }
    public string Body { get; set; } = string.Empty;
    public Guid? CreatedByUserId { get; set; }
    public string? CreatedByName { get; set; }
    public string? CreatedByEmail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

