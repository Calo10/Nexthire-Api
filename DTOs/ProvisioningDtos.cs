using System.ComponentModel.DataAnnotations;

namespace nexthire_api.DTOs;

public class ProvisionOrganizationRequestDto
{
    [Required]
    [StringLength(120, MinimumLength = 2)]
    public string Name { get; set; } = string.Empty;

    public string Timezone { get; set; } = "America/Costa_Rica";

    [Required]
    [EmailAddress]
    public string AdminEmail { get; set; } = string.Empty;

    public string? AdminFullName { get; set; }

    /// <summary>Send Nexa magic link to the admin after provisioning (default true).</summary>
    public bool SendLoginLink { get; set; } = true;

    public string? LoginCallbackUrl { get; set; }
}

public class ProvisionOrganizationResponseDto
{
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public Guid AdminUserId { get; set; }
    public string AdminEmail { get; set; } = string.Empty;
    public string? AdminFullName { get; set; }
    public bool AdminUserCreated { get; set; }
    public string AdminRole { get; set; } = "owner";
    public Guid? NextHireUserId { get; set; }
    public bool LoginLinkSent { get; set; }
}

public record NexaProvisionOrganizationResponse(
    Guid OrganizationId,
    string Name,
    string Slug,
    DateTimeOffset CreatedAt,
    Guid AdminUserId,
    string AdminEmail,
    string? AdminFullName,
    bool AdminUserCreated,
    string AdminRole);
