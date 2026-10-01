using System.ComponentModel.DataAnnotations;

namespace nexthire_api.DTOs;

public class CreateCandidateRequestDto
{
    [Required]
    [StringLength(160)]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    [StringLength(240)]
    public string LastName { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [StringLength(640)]
    public string Email { get; set; } = string.Empty;

    [StringLength(80)]
    public string? Phone { get; set; }

    [StringLength(160)]
    public string? Source { get; set; }

    [StringLength(1000)]
    public string? ResumeUrl { get; set; }
}

public class UpdateCandidateRequestDto
{
    [Required]
    [StringLength(160)]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    [StringLength(240)]
    public string LastName { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [StringLength(640)]
    public string Email { get; set; } = string.Empty;

    [StringLength(80)]
    public string? Phone { get; set; }

    [StringLength(160)]
    public string? Source { get; set; }

    [StringLength(1000)]
    public string? ResumeUrl { get; set; }
}

public class CandidateTagDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class AddCandidateTagRequestDto
{
    [Required]
    [StringLength(40, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;
}

public class CandidateNoteDto
{
    public Guid Id { get; set; }
    public string Body { get; set; } = string.Empty;
    public string? CreatedByName { get; set; }
    public string? CreatedByEmail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class AddCandidateNoteRequestDto
{
    [Required]
    [StringLength(4000, MinimumLength = 1)]
    public string Body { get; set; } = string.Empty;
}

public class CandidateDto
{
    public Guid Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Source { get; set; }
    public string? ResumeUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<CandidateTagDto> Tags { get; set; } = new();
}

public class CandidateListItemDto : CandidateDto
{
}

