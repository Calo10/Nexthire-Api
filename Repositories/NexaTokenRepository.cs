using Dapper;
using nexthire_api.Data;
using nexthire_api.Services;

namespace nexthire_api.Repositories;

public class NexaTokenRepository : INexaTokenRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public NexaTokenRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task EnsureSchemaAsync()
    {
        const string sql = @"
            IF OBJECT_ID('dbo.nexa_user_tokens', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.nexa_user_tokens (
                    nexa_user_id uniqueidentifier NOT NULL PRIMARY KEY,
                    access_token nvarchar(max) NOT NULL,
                    refresh_token nvarchar(max) NOT NULL,
                    expires_at datetimeoffset(7) NOT NULL,
                    updated_at datetimeoffset(7) NOT NULL
                );
            END";

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(sql);
    }

    public async Task UpsertAsync(string nexaUserId, string accessToken, string refreshToken, DateTimeOffset expiresAt)
    {
        if (!Guid.TryParse(nexaUserId, out var userId))
            return;

        const string sql = @"
            MERGE dbo.nexa_user_tokens AS target
            USING (SELECT @NexaUserId AS nexa_user_id) AS source
            ON target.nexa_user_id = source.nexa_user_id
            WHEN MATCHED THEN
                UPDATE SET
                    access_token = @AccessToken,
                    refresh_token = @RefreshToken,
                    expires_at = @ExpiresAt,
                    updated_at = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            WHEN NOT MATCHED THEN
                INSERT (nexa_user_id, access_token, refresh_token, expires_at, updated_at)
                VALUES (@NexaUserId, @AccessToken, @RefreshToken, @ExpiresAt, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'));";

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(sql, new
        {
            NexaUserId = userId,
            AccessToken = accessToken,
            RefreshToken = refreshToken ?? string.Empty,
            ExpiresAt = expiresAt
        });
    }

    public async Task<NexaTokenInfo?> GetAsync(string nexaUserId)
    {
        if (!Guid.TryParse(nexaUserId, out var userId))
            return null;

        const string sql = @"
            SELECT access_token AS AccessToken, refresh_token AS RefreshToken, expires_at AS ExpiresAt
            FROM dbo.nexa_user_tokens
            WHERE nexa_user_id = @NexaUserId;";

        using var connection = _connectionFactory.CreateConnection();
        var row = await connection.QueryFirstOrDefaultAsync<NexaTokenRow>(sql, new { NexaUserId = userId });
        if (row is null || string.IsNullOrWhiteSpace(row.AccessToken))
            return null;

        return new NexaTokenInfo
        {
            AccessToken = row.AccessToken,
            RefreshToken = row.RefreshToken ?? string.Empty,
            ExpiresAt = row.ExpiresAt
        };
    }

    public async Task RemoveAsync(string nexaUserId)
    {
        if (!Guid.TryParse(nexaUserId, out var userId))
            return;

        const string sql = @"DELETE FROM dbo.nexa_user_tokens WHERE nexa_user_id = @NexaUserId;";
        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(sql, new { NexaUserId = userId });
    }

    private sealed class NexaTokenRow
    {
        public string AccessToken { get; set; } = string.Empty;
        public string? RefreshToken { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
    }
}
