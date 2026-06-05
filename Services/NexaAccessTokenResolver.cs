using Microsoft.AspNetCore.Http;
using nexthire_api.Exceptions;
using nexthire_api.Repositories;

namespace nexthire_api.Services;

public interface INexaAccessTokenResolver
{
    Task<string?> GetValidAccessTokenAsync(
        string nexaUserId,
        string? accessTokenOverride = null,
        CancellationToken cancellationToken = default);
    Task PersistTokensAsync(string nexaUserId, string accessToken, string refreshToken, DateTimeOffset expiresAt);
}

public class NexaAccessTokenResolver : INexaAccessTokenResolver
{
    public const string NexaAccessTokenHeader = "X-Nexa-Access-Token";

    private readonly INexaTokenStore _tokenStore;
    private readonly INexaTokenRepository _tokenRepository;
    private readonly INexaClient _nexaClient;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<NexaAccessTokenResolver> _logger;

    public NexaAccessTokenResolver(
        INexaTokenStore tokenStore,
        INexaTokenRepository tokenRepository,
        INexaClient nexaClient,
        IHttpContextAccessor httpContextAccessor,
        ILogger<NexaAccessTokenResolver> logger)
    {
        _tokenStore = tokenStore;
        _tokenRepository = tokenRepository;
        _nexaClient = nexaClient;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task<string?> GetValidAccessTokenAsync(
        string nexaUserId,
        string? accessTokenOverride = null,
        CancellationToken cancellationToken = default)
    {
        var overrideToken = accessTokenOverride;
        if (string.IsNullOrWhiteSpace(overrideToken))
        {
            overrideToken = _httpContextAccessor.HttpContext?.Request.Headers[NexaAccessTokenHeader].FirstOrDefault();
        }

        if (!string.IsNullOrWhiteSpace(overrideToken))
        {
            _logger.LogDebug("Using explicit Nexa access token for user {UserId}", nexaUserId);
            await PersistTokensAsync(nexaUserId, overrideToken, string.Empty, DateTimeOffset.UtcNow.AddHours(1));
            return overrideToken;
        }

        var tokenInfo = _tokenStore.GetTokens(nexaUserId);
        if (tokenInfo is not null && !tokenInfo.IsExpired)
            return tokenInfo.AccessToken;

        if (tokenInfo is null)
        {
            tokenInfo = await _tokenRepository.GetAsync(nexaUserId);
            if (tokenInfo is not null)
            {
                _tokenStore.StoreTokens(
                    nexaUserId,
                    tokenInfo.AccessToken,
                    tokenInfo.RefreshToken,
                    tokenInfo.ExpiresAt);
            }
        }

        if (tokenInfo is not null && !tokenInfo.IsExpired)
            return tokenInfo.AccessToken;

        if (tokenInfo is null || string.IsNullOrEmpty(tokenInfo.RefreshToken))
            return null;

        try
        {
            var refreshResponse = await _nexaClient.RefreshTokenAsync(tokenInfo.RefreshToken, cancellationToken);
            await PersistTokensAsync(
                nexaUserId,
                refreshResponse.AccessToken,
                refreshResponse.RefreshToken,
                refreshResponse.ExpiresAt);
            return refreshResponse.AccessToken;
        }
        catch (HttpRequestException ex) when (ex.Data.Contains("IsNotImplemented") && ex.Data["IsNotImplemented"]?.Equals(true) == true)
        {
            _logger.LogWarning("Nexa refresh endpoint not available for user {UserId}", nexaUserId);
            return null;
        }
        catch (NexaAuthException ex)
        {
            _logger.LogWarning(ex, "Nexa token refresh failed for user {UserId}", nexaUserId);
            _tokenStore.RemoveTokens(nexaUserId);
            await _tokenRepository.RemoveAsync(nexaUserId);
            return null;
        }
    }

    public async Task PersistTokensAsync(string nexaUserId, string accessToken, string refreshToken, DateTimeOffset expiresAt)
    {
        _tokenStore.StoreTokens(nexaUserId, accessToken, refreshToken, expiresAt);
        await _tokenRepository.UpsertAsync(nexaUserId, accessToken, refreshToken, expiresAt);
    }
}
