using nexthire_api.DTOs;

namespace nexthire_api.Services;

public interface IOrgUserService
{
    Task<IReadOnlyList<OrgUserDto>> ListAsync(Guid orgId, string requesterNexaUserId, CancellationToken cancellationToken = default);
    Task<OrgUserDto?> GetAsync(Guid orgId, Guid userId, CancellationToken cancellationToken = default);
    Task<CreateOrgUserResponseDto> InviteAsync(
        Guid orgId,
        string requesterNexaUserId,
        CreateOrgUserRequestDto dto,
        CancellationToken cancellationToken = default);
    Task<OrgUserDto?> UpdateAsync(Guid orgId, Guid userId, UpdateOrgUserRequestDto dto, CancellationToken cancellationToken = default);
    Task<bool> RemoveInvitedAsync(Guid orgId, string requesterNexaUserId, Guid userId, string? nexaAccessTokenOverride = null, CancellationToken cancellationToken = default);
}
