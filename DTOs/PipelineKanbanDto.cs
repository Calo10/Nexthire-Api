using System.ComponentModel.DataAnnotations;

namespace nexthire_api.DTOs;

public class PipelineStageDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsSystem { get; set; }
}

public class ApplicationCardDto
{
    public Guid Id { get; set; }
    public Guid CandidateId { get; set; }
    public string CandidateName { get; set; } = string.Empty;
    public Guid JobId { get; set; }
    public string JobTitle { get; set; } = string.Empty;
    public Guid CurrentStageId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset? AppliedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class KanbanBoardDto
{
    public IReadOnlyList<PipelineStageDto> Stages { get; set; } = Array.Empty<PipelineStageDto>();
    public Dictionary<Guid, List<ApplicationCardDto>> Columns { get; set; } = new();
}

public class MoveApplicationRequest
{
    [Required]
    public Guid ToStageId { get; set; }
}

public class CreateApplicationRequest
{
    [Required]
    public Guid JobId { get; set; }

    [Required]
    public Guid CandidateId { get; set; }

    // Optional override; if null we use first stage by sort_order.
    public Guid? CurrentStageId { get; set; }

    // Optional override; default "active"
    [StringLength(40)]
    public string? Status { get; set; }
}

/// <summary>
/// Link an existing candidate to a job (candidate id comes from the URL).
/// Same behavior as <see cref="CreateApplicationRequest"/> but without duplicate candidateId in the body.
/// </summary>
public class CreateApplicationForCandidateRequest
{
    [Required]
    public Guid JobId { get; set; }

    public Guid? CurrentStageId { get; set; }

    [StringLength(40)]
    public string? Status { get; set; }
}

