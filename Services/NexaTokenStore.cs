using System.Collections.Concurrent;

namespace nexthire_api.Services;

public interface INexaTokenStore
{
    void StoreTokens(string userId, string accessToken, string refreshToken, DateTimeOffset expiresAt);
    NexaTokenInfo? GetTokens(string userId);
    void RemoveTokens(string userId);
}

public class NexaTokenInfo
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
}

public class NexaTokenStore : INexaTokenStore
{
    private readonly ConcurrentDictionary<string, NexaTokenInfo> _tokens = new();
    private readonly ILogger<NexaTokenStore> _logger;

    public NexaTokenStore(ILogger<NexaTokenStore> logger)
    {
        _logger = logger;
    }

    public void StoreTokens(string userId, string accessToken, string refreshToken, DateTimeOffset expiresAt)
    {
        _tokens.AddOrUpdate(userId, 
            new NexaTokenInfo 
            { 
                AccessToken = accessToken, 
                RefreshToken = refreshToken, 
                ExpiresAt = expiresAt 
            },
            (key, existing) => new NexaTokenInfo 
            { 
                AccessToken = accessToken, 
                RefreshToken = refreshToken, 
                ExpiresAt = expiresAt 
            });
        
        _logger.LogInformation("Stored Nexa tokens for user {UserId}. Expires at: {ExpiresAt}", userId, expiresAt);
    }

    public NexaTokenInfo? GetTokens(string userId)
    {
        _logger.LogInformation("GetTokens called for userId: {UserId}. Total tokens stored: {TokenCount}", userId, _tokens.Count);
        
        if (_tokens.TryGetValue(userId, out var tokenInfo))
        {
            _logger.LogInformation("Found token for userId: {UserId}. ExpiresAt: {ExpiresAt}, IsExpired: {IsExpired}", 
                userId, tokenInfo.ExpiresAt, tokenInfo.IsExpired);
            
            // Clean up expired tokens
            if (tokenInfo.IsExpired)
            {
                _tokens.TryRemove(userId, out _);
                _logger.LogInformation("Removed expired Nexa tokens for user {UserId}", userId);
                return null;
            }
            return tokenInfo;
        }
        
        _logger.LogWarning("No token found for userId: {UserId}. Available userIds: {UserIds}", 
            userId, string.Join(", ", _tokens.Keys));
        return null;
    }

    public void RemoveTokens(string userId)
    {
        _tokens.TryRemove(userId, out _);
        _logger.LogInformation("Removed Nexa tokens for user {UserId}", userId);
    }
}
