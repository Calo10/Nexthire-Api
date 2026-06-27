using nexthire_api.Options;

namespace nexthire_api.Services;

public interface ITwilioOrgCredentialsResolver
{
    Task<TwilioOrgCredentials> ResolveAsync(Guid orgId, CancellationToken cancellationToken = default);
}
