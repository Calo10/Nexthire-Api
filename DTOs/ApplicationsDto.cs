using System.ComponentModel.DataAnnotations;

namespace nexthire_api.DTOs;

public class ApplicationListItemDto
{
    public Guid ApplicationId { get; set; }
    public Guid JobId { get; set; }
    public string JobTitle { get; set; } = string.Empty;
    public Guid CandidateId { get; set; }
    public string CandidateName { get; set; } = string.Empty;
    public string CandidateEmail { get; set; } = string.Empty;
    public Guid CurrentStageId { get; set; }
    public string CurrentStageName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset? AppliedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class ApplicationDetailDto
{
    public ApplicationListItemDto Application { get; set; } = new();
    public IReadOnlyList<ApplicationStageHistoryItemDto> StageHistory { get; set; } = Array.Empty<ApplicationStageHistoryItemDto>();
}

public class ApplicationStageHistoryItemDto
{
    public Guid Id { get; set; }
    public Guid ApplicationId { get; set; }
    public Guid? FromStageId { get; set; }
    public string? FromStageName { get; set; }
    public Guid ToStageId { get; set; }
    public string ToStageName { get; set; } = string.Empty;
    public Guid MovedByUserId { get; set; }
    public string? MovedByName { get; set; }
    public string? MovedByEmail { get; set; }
    public DateTimeOffset MovedAt { get; set; }
}

public class CreateApplicationRequestDto
{
    [Required]
    public Guid JobId { get; set; }

    [Required]
    public Guid CandidateId { get; set; }

    [Required]
    public Guid CurrentStageId { get; set; }

    [Required]
    [StringLength(40)]
    public string Status { get; set; } = string.Empty;

    public DateTimeOffset? AppliedAt { get; set; }
}

public class UpdateApplicationRequestDto
{
    [StringLength(40)]
    public string? Status { get; set; }

    public Guid? CurrentStageId { get; set; }

    public DateTimeOffset? AppliedAt { get; set; }
}

public class MoveStageRequestDto
{
    [Required]
    public Guid ToStageId { get; set; }

    [StringLength(2000)]
    public string? Note { get; set; }
}

public class UpdateApplicationStatusRequestDto
{
    [Required]
    [StringLength(40)]
    public string Status { get; set; } = string.Empty;
}

