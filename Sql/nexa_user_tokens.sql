IF OBJECT_ID('dbo.nexa_user_tokens', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.nexa_user_tokens (
        nexa_user_id uniqueidentifier NOT NULL PRIMARY KEY,
        access_token nvarchar(max) NOT NULL,
        refresh_token nvarchar(max) NOT NULL,
        expires_at datetimeoffset(7) NOT NULL,
        updated_at datetimeoffset(7) NOT NULL
    );
END
