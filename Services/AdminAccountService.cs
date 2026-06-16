using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using nexthire_api.DTOs;
using nexthire_api.Repositories;

namespace nexthire_api.Services;

public class AdminAccountService : IAdminAccountService
{
    private readonly INexaClient _nexa;
    private readonly IUserRepository _users;
    private readonly IRoleRepository _roles;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdminAccountService> _logger;

    public AdminAccountService(
        INexaClient nexa,
        IUserRepository users,
        IRoleRepository roles,
        IConfiguration configuration,
        ILogger<AdminAccountService> logger)
    {
        _nexa = nexa;
        _users = users;
        _roles = roles;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<CreateAdminAccountResponseDto> CreateAdminAccountAsync(
        CreateAdminAccountRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidSecretKey(request.SecretKey))
            throw new UnauthorizedAccessException("Invalid secret key.");

        var email = request.Email.Trim().ToLowerInvariant();
        var password = request.Password;
        var fullName = string.IsNullOrWhiteSpace(request.FullName) ? null : request.FullName.Trim();

        NexaProvisionOrganizationResponse nexa;
        try
        {
            if (request.OrgId is Guid orgId && orgId != Guid.Empty)
            {
                nexa = await _nexa.ProvisionOrganizationMemberAsync(
                    orgId,
                    email,
                    fullName,
                    password,
                    cancellationToken: cancellationToken);
            }
            else
            {
                var orgName = ResolveOrgName(request.OrgName, email);
                var timezone = string.IsNullOrWhiteSpace(request.Timezone)
                    ? "America/Costa_Rica"
                    : request.Timezone.Trim();

                nexa = await _nexa.ProvisionOrganizationAsync(
                    orgName,
                    timezone,
                    email,
                    fullName,
                    password,
                    cancellationToken);
            }
        }
        catch (HttpRequestException ex)
        {
            throw MapNexaException(ex);
        }

        var nhUserId = await _users.UpsertFromNexaMemberAsync(
            nexa.OrganizationId,
            nexa.AdminUserId,
            nexa.AdminEmail,
            nexa.AdminFullName);

        await _roles.EnsureDefaultRolesAsync(nexa.OrganizationId);
        var orgRoles = await _roles.ListRolesAsync(nexa.OrganizationId);
        var adminRole = orgRoles.FirstOrDefault(r =>
            string.Equals(r.Code, "account_admin", StringComparison.OrdinalIgnoreCase));
        if (adminRole is not null && !await _roles.UserRoleExistsAsync(nexa.OrganizationId, nhUserId, adminRole.Id))
            await _roles.InsertUserRoleAsync(nexa.OrganizationId, nhUserId, adminRole.Id);

        _logger.LogInformation(
            "Admin account created via bootstrap. OrgId={OrgId} AdminUserId={AdminUserId} Email={Email}",
            nexa.OrganizationId, nexa.AdminUserId, email);

        return new CreateAdminAccountResponseDto
        {
            OrganizationId = nexa.OrganizationId,
            OrganizationName = nexa.Name,
            AdminUserId = nexa.AdminUserId,
            NextHireUserId = nhUserId,
            Email = nexa.AdminEmail,
            FullName = nexa.AdminFullName,
            AdminUserCreated = nexa.AdminUserCreated,
            AdminRole = nexa.AdminRole
        };
    }

    private bool IsValidSecretKey(string providedKey)
    {
        var configuredKey = _configuration["AdminBootstrap:ApiKey"]?.Trim()
                            ?? _configuration["Provisioning:ApiKey"]?.Trim();
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            _logger.LogError("AdminBootstrap:ApiKey (or Provisioning:ApiKey) is not configured");
            return false;
        }

        var providedBytes = Encoding.UTF8.GetBytes(providedKey.Trim());
        var configuredBytes = Encoding.UTF8.GetBytes(configuredKey);
        return providedBytes.Length == configuredBytes.Length
               && CryptographicOperations.FixedTimeEquals(providedBytes, configuredBytes);
    }

    private static string ResolveOrgName(string? orgName, string email)
    {
        if (!string.IsNullOrWhiteSpace(orgName))
            return orgName.Trim();

        var localPart = email.Split('@')[0];
        var label = string.IsNullOrWhiteSpace(localPart) ? "Admin" : char.ToUpper(localPart[0]) + localPart[1..];
        return $"{label} Organization";
    }

    private static Exception MapNexaException(HttpRequestException ex)
    {
        if (!ex.Data.Contains("StatusCode") || ex.Data["StatusCode"] is not System.Net.HttpStatusCode status)
        {
            return new InvalidOperationException(
                "Unable to reach Nexa. Verify Nexa:BaseUrl and that nexa-api is running.", ex);
        }

        var body = ex.Data["ErrorBody"]?.ToString();
        var message = TryExtractError(body) ?? $"Nexa returned {(int)status}.";

        return status switch
        {
            System.Net.HttpStatusCode.Unauthorized =>
                new InvalidOperationException(
                    "Nexa rejected the provisioning API key. Ensure Provisioning:ApiKey matches in nexthire-api and nexa-api."),
            System.Net.HttpStatusCode.BadRequest => new ArgumentException(message),
            System.Net.HttpStatusCode.NotFound =>
                new InvalidOperationException(
                    "Nexa endpoint POST /v1/orgs/{orgId}/members/provision was not found. Deploy nexa-api commit with 'Add existing org'."),
            _ => new InvalidOperationException(message, ex)
        };
    }

    private static string? TryExtractError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
                return err.GetString();
        }
        catch
        {
            return body.Length > 200 ? body[..200] : body;
        }

        return null;
    }
}
