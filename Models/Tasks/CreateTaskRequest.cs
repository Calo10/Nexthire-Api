using System.ComponentModel.DataAnnotations;

namespace nexthire_api.Models.Tasks;

public class CreateTaskRequest
{
    [Required]
    [StringLength(400)]
    public string Title { get; set; } = string.Empty;

    // allowed: todo | in_progress | blocked | done
    public string? Status { get; set; }

    public DateTimeOffset? DueAt { get; set; }

    [Required]
    public Guid ApplicationId { get; set; }

    public Guid? AssignedToUserId { get; set; }
}

