using System.ComponentModel.DataAnnotations;

namespace nexthire_api.Models.Public;

public class ApplyJobRequest
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

