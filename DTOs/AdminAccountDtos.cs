using System.ComponentModel.DataAnnotations;

namespace nexthire_api.DTOs;

public class CreateAdminAccountRequestDto
{
    [Required]
    public string SecretKey { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    [StringLength(128, MinimumLength = 8)]
    public string Password { get; set; } = string.Empty;

    [StringLength(120, MinimumLength = 2)]
    public string? OrgName { get; set; }

    public string? FullName { get; set; }

    public string Timezone { get; set; } = "America/Costa_Rica";
}

public class CreateAdminAccountResponseDto
{
    public Guid OrganizationId { get; set; }
    public string OrganizationName { get; set; } = string.Empty;
    public Guid AdminUserId { get; set; }
    public Guid? NextHireUserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public bool AdminUserCreated { get; set; }
    public string AdminRole { get; set; } = "owner";
}
