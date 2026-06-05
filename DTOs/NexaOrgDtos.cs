namespace nexthire_api.DTOs;

public class NexaOrgMemberDto
{
    public Guid MemberId { get; set; }
    public Guid UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public string Role { get; set; } = string.Empty;
    public DateTimeOffset JoinedAt { get; set; }
}

public class NexaOrgInviteDto
{
    public Guid InviteId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Status { get; set; } = string.Empty;
}
