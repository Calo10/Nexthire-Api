using nexthire_api.Repositories;

namespace nexthire_api.Services;

public class OrgMembershipAuthorizationService : IOrgMembershipAuthorizationService
{
    private readonly INexaClient _nexa;
    private readonly INexaAccessTokenResolver _nexaTokens;
    private readonly ILogger<OrgMembershipAuthorizationService> _logger;

    public OrgMembershipAuthorizationService(
        INexaClient nexa,
        INexaAccessTokenResolver nexaTokens,
        ILogger<OrgMembershipAuthorizationService> logger)
    {
        _nexa = nexa;
        _nexaTokens = nexaTokens;
        _logger = logger;
    }

    public async Task EnsureOwnerOrAdminAsync(
        Guid orgId,
        Guid nexaUserId,
        CancellationToken cancellationToken = default)
    {
        var nexaToken = await _nexaTokens.GetValidAccessTokenAsync(nexaUserId.ToString(), cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(nexaToken))
        {
            throw new UnauthorizedAccessException(
                "Nexa access token is required to verify organization permissions. Sign in again or send the X-Nexa-Access-Token header.");
        }

        IReadOnlyList<DTOs.NexaOrgMemberDto> members;
        try
        {
            members = await _nexa.ListOrgMembersAsync(orgId, nexaToken, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to verify Nexa org role for user {UserId} in org {OrgId}",
                nexaUserId,
                orgId);
            throw new UnauthorizedAccessException("Unable to verify organization permissions.");
        }

        var member = members.FirstOrDefault(m => m.UserId == nexaUserId);
        if (member is null)
            throw new UnauthorizedAccessException("You are not a member of this organization.");

        var role = member.Role.Trim().ToLowerInvariant();
        if (role is not ("owner" or "admin"))
            throw new UnauthorizedAccessException("Only organization owners and admins can manage settings.");
    }
}
