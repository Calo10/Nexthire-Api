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
    private readonly ILogger<OrgUserService> _logger;

    public OrgUserService(
        IUserRepository users,
        IRoleRepository roles,
        ITeamRepository teams,
        INexaClient nexa,
        INexaAccessTokenResolver nexaTokens,
        IEmailService email,
        ILogger<OrgUserService> logger)
    {
        _users = users;
        _roles = roles;
        _teams = teams;
        _nexa = nexa;
        _nexaTokens = nexaTokens;
        _email = email;
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
        catch (HttpRequestException ex) when (ex.Data["StatusCode"] is System.Net.HttpStatusCode.Unauthorized
                                              or System.Net.HttpStatusCode.Forbidden)
        {
            _logger.LogWarning(ex, "Nexa rejected org members sync for org {OrgId}; returning local nh_users", orgId);
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
            await _users.UpsertFromNexaMemberAsync(orgId, member.UserId, member.Email, member.FullName);
        }

        foreach (var invite in pendingInvites)
        {
            var email = invite.Email.Trim().ToLowerInvariant();
            await _users.UpsertPendingByEmailAsync(orgId, email, null, null, null);
        }

        var users = await _users.ListByOrgAsync(orgId);
        var memberByNexaId = members.ToDictionary(m => m.UserId);
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

        var nexaToken = await RequireNexaTokenAsync(requesterNexaUserId, dto.NexaAccessToken, cancellationToken);
        NexaOrgInviteDto invite;
        try
        {
            invite = await _nexa.SendOrgInviteAsync(orgId, email, nexaRole, nexaToken, cancellationToken)
                     ?? throw new InvalidOperationException("Nexa did not return an invite.");
        }
        catch (HttpRequestException ex) when (ex.Data.Contains("StatusCode"))
        {
            throw MapNexaHttpException(ex);
        }

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
                await _nexa.RequestMagicLinkAsync(email, cancellationToken: cancellationToken);
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
