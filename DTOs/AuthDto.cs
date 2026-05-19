using System.Text.Json.Serialization;

namespace nexthire_api.DTOs;

public class MagicLinkRequestDto
{
    public string Email { get; set; } = string.Empty;
    public string? CallbackUrl { get; set; }
}

public class ExchangeTokenRequestDto
{
    public string Token { get; set; } = string.Empty;
}

public class ExchangeTokenResponseDto
{
    public string AccessToken { get; set; } = string.Empty;
    public NexaTokensDto Nexa { get; set; } = null!;
    public bool RequiresOrgSetup { get; set; }
    public NexaUserDto? User { get; set; }
    public NexaOrganizationDto? Organization { get; set; }
    public object? Features { get; set; }
}

public class NexaTokensDto
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
}

// DTOs for Nexa API responses (strongly typed records)
public record NexaExchangeResponseDto(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    bool RequiresOrgSetup,
    NexaUserDto User,
    NexaOrganizationDto? Organization,
    object? Features
);

public record NexaUserDto(
    string UserId,
    string Email,
    string? FullName
);

public class NexaOrganizationDto
{
    // Nexa may return either `id` or `organizationId` depending on the endpoint/version.
    // Support both, so NextHire can reliably emit `nexa_org_id` in its JWT.
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("organizationId")]
    public string? OrganizationId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    public string? GetOrgId() => !string.IsNullOrWhiteSpace(Id) ? Id : OrganizationId;
}

public class PasswordResetRequestDto
{
    public string Email { get; set; } = string.Empty;
}

public class PasswordResetConfirmDto
{
    public string Token { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

public class PasswordLoginRequestDto
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class SetPasswordRequestDto
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
}

public class InitializePasswordRequestDto
{
    public string NewPassword { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
}
