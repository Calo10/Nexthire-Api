namespace nexthire_api.Models.Notes;

public class CreateNoteRequest
{
    public Guid ApplicationId { get; set; }
    public string Body { get; set; } = string.Empty;
}

