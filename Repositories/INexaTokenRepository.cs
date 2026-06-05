using nexthire_api.Services;

namespace nexthire_api.Repositories;

public interface INexaTokenRepository
{
    Task EnsureSchemaAsync();
    Task UpsertAsync(string nexaUserId, string accessToken, string refreshToken, DateTimeOffset expiresAt);
    Task<NexaTokenInfo?> GetAsync(string nexaUserId);
    Task RemoveAsync(string nexaUserId);
}
