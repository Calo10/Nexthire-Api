namespace nexthire_api.Services;

public interface IOrgMembershipAuthorizationService
{
    Task EnsureOwnerOrAdminAsync(
        Guid orgId,
        Guid nexaUserId,
        CancellationToken cancellationToken = default);
}
