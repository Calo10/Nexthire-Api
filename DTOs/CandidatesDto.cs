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
}

public class CandidateListItemDto : CandidateDto
{
}

