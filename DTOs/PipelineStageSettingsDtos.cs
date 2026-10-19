using System.ComponentModel.DataAnnotations;

namespace nexthire_api.DTOs;

public class PipelineStageSettingsDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsSystem { get; set; }
    public int ApplicationCount { get; set; }
    public int HistoryCount { get; set; }
    public bool CanDelete { get; set; }
    public string? DeleteBlockedReason { get; set; }
}

public class CreatePipelineStageRequest
{
    [Required]
    [StringLength(60)]
    public string Name { get; set; } = string.Empty;
}

public class RenamePipelineStageRequest
{
    [Required]
    [StringLength(60)]
    public string Name { get; set; } = string.Empty;
}

public class ReorderPipelineStagesRequest
{
    [Required]
    public List<Guid> StageIds { get; set; } = new();
}
