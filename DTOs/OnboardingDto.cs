namespace nexthire_api.DTOs;

public class CreateOrganizationRequestDto
{
    public string Name { get; set; } = string.Empty;
    public string Timezone { get; set; } = string.Empty;
}

public class CreateOrganizationResponseDto
{
    public object Organization { get; set; } = null!;
    public object? Features { get; set; }
    public bool RequiresOrgSetup { get; set; }
}

// DTO for Nexa API create organization response
public record NexaCreateOrgResponse(
    string OrganizationId,
    string Name,
    string Slug,
    DateTime CreatedAt,
    string Role,
    bool IsActive,
    object? Features
);
