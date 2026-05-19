using System.Collections.Concurrent;

namespace nexthire_api.Services;

public interface IAuthLinkTokenStore
{
    void StoreMagicLinkToken(string token, string email, DateTimeOffset expiresAt);
    void StorePasswordResetToken(string token, string email, DateTimeOffset expiresAt);
}

public class AuthLinkTokenStore : IAuthLinkTokenStore
{
    private readonly ConcurrentDictionary<string, StoredAuthLinkToken> _tokens = new();
    private readonly ILogger<AuthLinkTokenStore> _logger;

    public AuthLinkTokenStore(ILogger<AuthLinkTokenStore> logger)
    {
        _logger = logger;
    }

    public void StoreMagicLinkToken(string token, string email, DateTimeOffset expiresAt)
    {
        StoreToken("magic-link", token, email, expiresAt);
    }

    public void StorePasswordResetToken(string token, string email, DateTimeOffset expiresAt)
    {
        StoreToken("password-reset", token, email, expiresAt);
    }

    private void StoreToken(string tokenType, string token, string email, DateTimeOffset expiresAt)
    {
        CleanupExpiredTokens();

        _tokens[token] = new StoredAuthLinkToken
        {
            TokenType = tokenType,
            Email = email,
            ExpiresAt = expiresAt
        };

        _logger.LogInformation(
            "Stored auth link token. Type: {TokenType}, Email: {Email}, ExpiresAt: {ExpiresAt}",
            tokenType,
            email,
            expiresAt);
    }

    private void CleanupExpiredTokens()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _tokens)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _tokens.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed class StoredAuthLinkToken
    {
        public string TokenType { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public DateTimeOffset ExpiresAt { get; set; }
    }
}
