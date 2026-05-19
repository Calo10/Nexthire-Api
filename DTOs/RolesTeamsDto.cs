using System.ComponentModel.DataAnnotations;

namespace nexthire_api.DTOs;

public class RoleDto
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class UserRoleDto
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public string RoleCode { get; set; } = string.Empty;
    public string? RoleName { get; set; }
    public DateTimeOffset AssignedAt { get; set; }
}

public class AssignUserRoleRequestDto
{
    [Required]
    public Guid RoleId { get; set; }
}

public class TeamDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class CreateTeamRequestDto
{
    [Required]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [StringLength(4000)]
    public string? Description { get; set; }
}

public class UpdateTeamRequestDto
{
    [Required]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [StringLength(4000)]
    public string? Description { get; set; }
}

public class TeamMemberDto
{
    public Guid UserId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public bool IsTeamLead { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
}

public class AddTeamMemberRequestDto
{
    [Required]
    public Guid UserId { get; set; }

    public bool IsTeamLead { get; set; }
}
