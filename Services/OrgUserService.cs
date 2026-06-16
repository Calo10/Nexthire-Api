using nexthire_api.DTOs;
using nexthire_api.Repositories;

namespace nexthire_api.Services;

public class OrgUserService : IOrgUserService
{
    private readonly IUserRepository _users;
    private readonly IRoleRepository _roles;
    private readonly ITeamRepository _teams;
    private readonly INexaClient _nexa;
    private readonly INexaAccessTokenResolver _nexaTokens;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OrgUserService> _logger;

    public OrgUserService(
        IUserRepository users,
        IRoleRepository roles,
        ITeamRepository teams,
        INexaClient nexa,
        INexaAccessTokenResolver nexaTokens,
        IEmailService email,
        IConfiguration configuration,
        ILogger<OrgUserService> logger)
    {
        _users = users;
        _roles = roles;
        _teams = teams;
        _nexa = nexa;
        _nexaTokens = nexaTokens;
        _email = email;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<IReadOnlyList<OrgUserDto>> ListAsync(
        Guid orgId,
        string requesterNexaUserId,
        CancellationToken cancellationToken = default)
    {
        var nexaToken = await _nexaTokens.GetValidAccessTokenAsync(requesterNexaUserId, cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(nexaToken))
        {
            _logger.LogWarning(
                "No Nexa access token for user {UserId}; listing nh_users from local DB only. Send {Header} or sign in again for full sync.",
                requesterNexaUserId,
                NexaAccessTokenResolver.NexaAccessTokenHeader);
            return await _users.ListByOrgAsync(orgId);
        }

        IReadOnlyList<NexaOrgMemberDto> members;
        try
        {
            members = await _nexa.ListOrgMembersAsync(orgId, nexaToken, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                ex,
                "Nexa org members sync failed for org {OrgId} (Status={Status}); returning local nh_users",
                orgId,
                ex.Data["StatusCode"]);
            return await _users.ListByOrgAsync(orgId);
        }

        IReadOnlyList<NexaOrgInviteDto> pendingInvites;
        try
        {
            pendingInvites = await _nexa.ListOrgInvitesAsync(orgId, nexaToken, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                ex,
                "Nexa pending invites sync unavailable for org {OrgId} (Status={Status}); using local nh_users only for invited users",
                orgId,
                ex.Data["StatusCode"]);
            pendingInvites = [];
        }

        foreach (var member in members)
        {
            try
            {
                await _users.UpsertFromNexaMemberAsync(orgId, member.UserId, member.Email, member.FullName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to upsert nh_users for Nexa member {UserId} in org {OrgId}",
                    member.UserId,
                    orgId);
            }
        }

        foreach (var invite in pendingInvites)
        {
            try
            {
                var email = invite.Email.Trim().ToLowerInvariant();
                await _users.UpsertPendingByEmailAsync(orgId, email, null, null, null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to upsert pending nh_users for invite {Email} in org {OrgId}",
                    invite.Email,
                    orgId);
            }
        }

        var users = await _users.ListByOrgAsync(orgId);
        var memberByNexaId = members
            .GroupBy(m => m.UserId)
            .ToDictionary(g => g.Key, g => g.First());
        var memberByEmail = members
            .GroupBy(m => m.Email.Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First());
        var inviteByEmail = pendingInvites
            .GroupBy(i => i.Email.Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.OrderByDescending(i => i.CreatedAt).First());

        foreach (var u in users)
        {
            var emailKey = u.Email.Trim().ToLowerInvariant();
            if (u.NexaUserId.HasValue && memberByNexaId.TryGetValue(u.NexaUserId.Value, out var byId))
            {
                u.NexaOrgRole = byId.Role;
                u.Status = "active";
            }
            else if (memberByEmail.TryGetValue(emailKey, out var byEmail))
            {
                u.NexaOrgRole = byEmail.Role;
                u.Status = "active";
            }
            else if (inviteByEmail.TryGetValue(emailKey, out var byInvite))
            {
                u.NexaOrgRole = byInvite.Role;
                u.Status = "invited";
            }
            else if (!u.NexaUserId.HasValue)
            {
                u.Status = "invited";
            }
        }

        return users;
    }

    public Task<OrgUserDto?> GetAsync(Guid orgId, Guid userId, CancellationToken cancellationToken = default)
    {
        return _users.GetByIdAsync(orgId, userId);
    }

    public async Task<CreateOrgUserResponseDto> InviteAsync(
        Guid orgId,
        string requesterNexaUserId,
        CreateOrgUserRequestDto dto,
        CancellationToken cancellationToken = default)
    {
        var email = dto.Email.Trim().ToLowerInvariant();
        var nexaRole = string.IsNullOrWhiteSpace(dto.NexaOrgRole) ? "member" : dto.NexaOrgRole.Trim().ToLowerInvariant();
        if (nexaRole is not ("admin" or "member"))
            throw new ArgumentException("nexaOrgRole must be admin or member.");

        var existingUser = await _users.GetByEmailAsync(orgId, email);
        if (existingUser?.NexaUserId.HasValue == true)
            throw new ArgumentException("User is already an active member of this organization.");

        var nexaToken = await RequireNexaTokenAsync(requesterNexaUserId, dto.NexaAccessToken, cancellationToken);
        var invite = await ResolveOrCreateInviteAsync(orgId, email, nexaRole, nexaToken, cancellationToken);

        var nhUserId = await _users.UpsertPendingByEmailAsync(orgId, email, dto.FirstName, dto.LastName, dto.Phone);

        if (dto.NextHireRoleId.HasValue)
        {
            await _roles.EnsureDefaultRolesAsync(orgId);
            if (await _roles.GetRoleByIdAsync(orgId, dto.NextHireRoleId.Value) is null)
                throw new ArgumentException("nextHireRoleId not found for this organization.");

            if (!await _roles.UserRoleExistsAsync(orgId, nhUserId, dto.NextHireRoleId.Value))
                await _roles.InsertUserRoleAsync(orgId, nhUserId, dto.NextHireRoleId.Value);
        }

        if (dto.TeamIds is { Count: > 0 })
        {
            foreach (var teamId in dto.TeamIds.Distinct())
            {
                if (await _teams.GetTeamByIdAsync(orgId, teamId) is null)
                    throw new ArgumentException($"teamId {teamId} not found for this organization.");

                if (!await _teams.TeamMembershipExistsAsync(orgId, teamId, nhUserId))
                    await _teams.InsertMemberAsync(orgId, teamId, nhUserId, isTeamLead: false);
            }
        }

        if (dto.SendLoginLink)
        {
            try
            {
                var callbackUrl = ResolveLoginCallbackUrl(dto.LoginCallbackUrl);
                await _nexa.RequestMagicLinkAsync(email, callbackUrl, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invite created but magic link email failed for {Email}", email);
            }
        }

        var user = await _users.GetByIdAsync(orgId, nhUserId)
                   ?? throw new InvalidOperationException("Failed to load invited user.");

        user.NexaOrgRole = nexaRole;
        user.Status = "invited";

        _logger.LogInformation(
            "Org user invited. OrgId={OrgId} Email={Email} InviteId={InviteId} NhUserId={NhUserId}",
            orgId, email, invite.InviteId, nhUserId);

        return new CreateOrgUserResponseDto { User = user, Invite = invite };
    }

    public Task<OrgUserDto?> UpdateAsync(Guid orgId, Guid userId, UpdateOrgUserRequestDto dto, CancellationToken cancellationToken = default)
    {
        return _users.UpdateProfileAsync(orgId, userId, dto);
    }

    public async Task<bool> RemoveInvitedAsync(
        Guid orgId,
        string requesterNexaUserId,
        Guid userId,
        string? nexaAccessTokenOverride = null,
        CancellationToken cancellationToken = default)
    {
        var user = await _users.GetByIdAsync(orgId, userId);
        if (user is null)
            return false;

        if (user.NexaUserId.HasValue)
        {
            throw new ArgumentException(
                "Cannot remove an active organization member. Only pending invited users can be removed.");
        }

        var nexaToken = await RequireNexaTokenAsync(requesterNexaUserId, nexaAccessTokenOverride, cancellationToken);
        var emailKey = user.Email.Trim().ToLowerInvariant();

        try
        {
            var pendingInvites = await _nexa.ListOrgInvitesAsync(orgId, nexaToken, cancellationToken);
            var invite = pendingInvites
                .Where(i => i.Email.Trim().ToLowerInvariant() == emailKey)
                .OrderByDescending(i => i.CreatedAt)
                .FirstOrDefault();

            if (invite is not null)
            {
                try
                {
                    await _nexa.RevokeOrgInviteAsync(orgId, invite.InviteId, nexaToken, cancellationToken);
                }
                catch (HttpRequestException ex) when (ex.Data.Contains("StatusCode"))
                {
                    throw MapNexaHttpException(ex);
                }
            }
            else
            {
                _logger.LogWarning(
                    "No pending Nexa invite for {Email} in org {OrgId}; removing local nh_users row only",
                    user.Email,
                    orgId);
            }
        }
        catch (HttpRequestException ex) when (ex.Data["StatusCode"] is System.Net.HttpStatusCode.Unauthorized
                                              or System.Net.HttpStatusCode.Forbidden)
        {
            throw MapNexaHttpException(ex);
        }

        var deleted = await _users.DeleteFromOrgAsync(orgId, userId);
        if (deleted)
        {
            _logger.LogInformation(
                "Removed invited org user. OrgId={OrgId} UserId={UserId} Email={Email}",
                orgId,
                userId,
                user.Email);
        }

        return deleted;
    }

    private async Task<NexaOrgInviteDto> ResolveOrCreateInviteAsync(
        Guid orgId,
        string email,
        string nexaRole,
        string nexaToken,
        CancellationToken cancellationToken)
    {
        var pendingInvite = await FindPendingInviteAsync(orgId, email, nexaToken, cancellationToken);
        if (pendingInvite is not null)
        {
            _logger.LogInformation("Reusing existing Nexa pending invite for {Email} in org {OrgId}", email, orgId);
            return pendingInvite;
        }

        try
        {
            return await _nexa.SendOrgInviteAsync(orgId, email, nexaRole, nexaToken, cancellationToken)
                   ?? throw new InvalidOperationException("Nexa did not return an invite.");
        }
        catch (HttpRequestException ex) when (ex.Data.Contains("StatusCode"))
        {
            var recovered = await FindPendingInviteAsync(orgId, email, nexaToken, cancellationToken);
            if (recovered is not null)
            {
                _logger.LogWarning(
                    ex,
                    "Nexa invite create failed for {Email} but pending invite exists; continuing with existing invite",
                    email);
                return recovered;
            }

            throw MapNexaHttpException(ex);
        }
        catch (HttpRequestException ex)
        {
            var recovered = await FindPendingInviteAsync(orgId, email, nexaToken, cancellationToken);
            if (recovered is not null)
            {
                _logger.LogWarning(
                    ex,
                    "Nexa invite create failed for {Email} but pending invite exists; continuing with existing invite",
                    email);
                return recovered;
            }

            throw;
        }
    }

    private async Task<NexaOrgInviteDto?> FindPendingInviteAsync(
        Guid orgId,
        string email,
        string nexaToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var members = await _nexa.ListOrgMembersAsync(orgId, nexaToken, cancellationToken);
            if (members.Any(m => string.Equals(m.Email.Trim(), email, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("User is already an active member of this organization.");
        }
        catch (HttpRequestException ex) when (ex.Data["StatusCode"] is System.Net.HttpStatusCode.Unauthorized
                                              or System.Net.HttpStatusCode.Forbidden)
        {
            throw MapNexaHttpException(ex);
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Could not verify Nexa members before invite for org {OrgId}", orgId);
        }

        try
        {
            var pendingInvites = await _nexa.ListOrgInvitesAsync(orgId, nexaToken, cancellationToken);
            return pendingInvites
                .Where(i => string.Equals(i.Email.Trim(), email, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(i => i.CreatedAt)
                .FirstOrDefault();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Could not list Nexa pending invites for org {OrgId}", orgId);
            return null;
        }
    }

    private string ResolveLoginCallbackUrl(string? overrideUrl)
    {
        var callback = string.IsNullOrWhiteSpace(overrideUrl) ? null : overrideUrl.Trim();
        if (!string.IsNullOrEmpty(callback))
            return callback;

        var frontendBase = _configuration["Frontend:BaseUrl"]
                           ?? _configuration["AppSettings:FrontendUrl"]
                           ?? throw new InvalidOperationException(
                               "Frontend:BaseUrl is not configured. Set it in Azure App Settings (Frontend__BaseUrl) or pass loginCallbackUrl in the invite request.");

        return $"{frontendBase.Trim().TrimEnd('/')}/auth/callback";
    }

    private async Task<string> RequireNexaTokenAsync(
        string nexaUserId,
        string? accessTokenOverride,
        CancellationToken cancellationToken)
    {
        var token = await _nexaTokens.GetValidAccessTokenAsync(nexaUserId, accessTokenOverride, cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new UnauthorizedAccessException(
                "Nexa session expired. Sign in again, or send header X-Nexa-Access-Token (or body nexaAccessToken) from login/consume.");
        }

        return token;
    }

    private static Exception MapNexaHttpException(HttpRequestException ex)
    {
        if (!ex.Data.Contains("StatusCode") || ex.Data["StatusCode"] is not System.Net.HttpStatusCode status)
            return ex;

        var body = ex.Data["ErrorBody"]?.ToString();
        var message = string.IsNullOrWhiteSpace(body)
            ? $"Nexa returned {(int)status}."
            : body;

        return status switch
        {
            System.Net.HttpStatusCode.BadRequest => new ArgumentException(message),
            System.Net.HttpStatusCode.Unauthorized => new UnauthorizedAccessException("Nexa session expired or invalid."),
            System.Net.HttpStatusCode.Forbidden => new UnauthorizedAccessException("You do not have permission to manage users in this organization."),
            System.Net.HttpStatusCode.NotFound => new ArgumentException("Organization not found in Nexa or invite could not be created."),
            _ => ex
        };
    }
}
