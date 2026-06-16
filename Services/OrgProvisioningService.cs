using System.Text.Json;
using nexthire_api.DTOs;
using nexthire_api.Repositories;

namespace nexthire_api.Services;

public class OrgProvisioningService : IOrgProvisioningService
{
    private readonly INexaClient _nexa;
    private readonly IUserRepository _users;
    private readonly IRoleRepository _roles;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OrgProvisioningService> _logger;

    public OrgProvisioningService(
        INexaClient nexa,
        IUserRepository users,
        IRoleRepository roles,
        IConfiguration configuration,
        ILogger<OrgProvisioningService> logger)
    {
        _nexa = nexa;
        _users = users;
        _roles = roles;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ProvisionOrganizationResponseDto> ProvisionAsync(
        ProvisionOrganizationRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var name = request.Name.Trim();
        var email = request.AdminEmail.Trim().ToLowerInvariant();
        var timezone = string.IsNullOrWhiteSpace(request.Timezone)
            ? "America/Costa_Rica"
            : request.Timezone.Trim();
        var fullName = string.IsNullOrWhiteSpace(request.AdminFullName) ? null : request.AdminFullName.Trim();

        NexaProvisionOrganizationResponse nexa;
        try
        {
            nexa = await _nexa.ProvisionOrganizationAsync(name, timezone, email, fullName, null, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.Data.Contains("StatusCode"))
        {
            throw MapNexaProvisionException(ex);
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

        var loginLinkSent = false;
        if (request.SendLoginLink)
        {
            try
            {
                var callbackUrl = ResolveLoginCallbackUrl(request.LoginCallbackUrl);
                await _nexa.RequestMagicLinkAsync(email, callbackUrl, cancellationToken);
                loginLinkSent = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Provision OK but magic link failed for {Email}", email);
            }
        }

        return new ProvisionOrganizationResponseDto
        {
            OrganizationId = nexa.OrganizationId,
            Name = nexa.Name,
            Slug = nexa.Slug,
            CreatedAt = nexa.CreatedAt,
            AdminUserId = nexa.AdminUserId,
            AdminEmail = nexa.AdminEmail,
            AdminFullName = nexa.AdminFullName,
            AdminUserCreated = nexa.AdminUserCreated,
            AdminRole = nexa.AdminRole,
            NextHireUserId = nhUserId,
            LoginLinkSent = loginLinkSent
        };
    }

    private string ResolveLoginCallbackUrl(string? overrideUrl)
    {
        var callback = string.IsNullOrWhiteSpace(overrideUrl) ? null : overrideUrl.Trim();
        if (!string.IsNullOrEmpty(callback))
            return callback;

        var frontendBase = _configuration["Frontend:BaseUrl"]
                           ?? _configuration["AppSettings:FrontendUrl"]
                           ?? throw new InvalidOperationException(
                               "Frontend:BaseUrl is not configured. Set Frontend__BaseUrl in Azure App Settings or pass loginCallbackUrl.");

        return $"{frontendBase.Trim().TrimEnd('/')}/auth/callback";
    }

    private static Exception MapNexaProvisionException(HttpRequestException ex)
    {
        if (!ex.Data.Contains("StatusCode") || ex.Data["StatusCode"] is not System.Net.HttpStatusCode status)
            return ex;

        var body = ex.Data["ErrorBody"]?.ToString();
        var message = TryExtractError(body) ?? $"Nexa returned {(int)status}.";

        return status switch
        {
            System.Net.HttpStatusCode.Unauthorized =>
                new InvalidOperationException(
                    "Nexa rejected the provisioning API key. Ensure Provisioning:ApiKey matches nexa-api (note: appsettings.Development.json overrides appsettings.json)."),
            System.Net.HttpStatusCode.BadRequest => new ArgumentException(message),
            _ => ex
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
