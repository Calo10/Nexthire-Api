using System.ComponentModel.DataAnnotations;

namespace nexthire_api.DTOs;

public class OrgUserDto
{
    public Guid Id { get; set; }
    public Guid? NexaUserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    /// <summary>Nexa org membership role: owner, admin, member.</summary>
    public string? NexaOrgRole { get; set; }
    /// <summary>active = member in Nexa; invited = pending invite, not yet in org.</summary>
    public string Status { get; set; } = "active";
    public IReadOnlyList<UserRoleDto> Roles { get; set; } = Array.Empty<UserRoleDto>();
    public DateTimeOffset CreatedAt { get; set; }
}

public class CreateOrgUserRequestDto
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Phone { get; set; }

    /// <summary>Nexa org role for the invite: admin or member (default member).</summary>
    [RegularExpression("^(admin|member)$", ErrorMessage = "nexaOrgRole must be admin or member.")]
    public string NexaOrgRole { get; set; } = "member";

    /// <summary>Optional NextHire role to assign in this org (e.g. recruiter).</summary>
    public Guid? NextHireRoleId { get; set; }

    /// <summary>Optional teams to add the invited user to (nh_users.id is created before assignment).</summary>
    public IList<Guid>? TeamIds { get; set; }

    /// <summary>Send Nexa magic link email after creating the invite.</summary>
    public bool SendLoginLink { get; set; } = true;

    /// <summary>
    /// Where Nexa should redirect after the user opens the magic link (e.g. https://app.example.com/auth/callback).
    /// Falls back to Frontend:BaseUrl + /auth/callback when omitted.
    /// </summary>
    public string? LoginCallbackUrl { get; set; }

    /// <summary>Optional Nexa access token (from login/consume). Prefer header X-Nexa-Access-Token.</summary>
    public string? NexaAccessToken { get; set; }
}

public class UpdateOrgUserRequestDto
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Phone { get; set; }
}

public class CreateOrgUserResponseDto
{
    public OrgUserDto User { get; set; } = null!;
    public NexaOrgInviteDto Invite { get; set; } = null!;
}
