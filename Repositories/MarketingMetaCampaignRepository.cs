using Dapper;
using nexthire_api.Data;

namespace nexthire_api.Repositories;

public interface IMarketingMetaCampaignRepository
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken = default);

    Task InsertAsync(MarketingMetaCampaignRow row, CancellationToken cancellationToken = default);

    Task<MarketingMetaCampaignRow?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<MarketingMetaCampaignRow?> GetByMetaCampaignIdAsync(
        string metaCampaignId,
        CancellationToken cancellationToken = default);

    Task<MarketingMetaCampaignRow?> GetLatestByJobIdForTenantAsync(
        Guid jobId,
        string tenantId,
        CancellationToken cancellationToken = default);

    Task<MarketingMetaCampaignRow?> GetByMetaCampaignIdForTenantAsync(
        string metaCampaignId,
        string tenantId,
        CancellationToken cancellationToken = default);

    Task<MarketingMetaCampaignRow?> GetByIdForTenantAsync(Guid id, string tenantId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MarketingMetaCampaignRow>> ListByTenantAsync(
        string tenantId,
        Guid? jobId,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteByIdForTenantAsync(Guid id, string tenantId, CancellationToken cancellationToken = default);

    Task<bool> DeleteByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> UpdateStatusForTenantAsync(
        Guid id,
        string tenantId,
        string status,
        CancellationToken cancellationToken = default);

    Task<bool> UpdateStatusByIdAsync(Guid id, string status, CancellationToken cancellationToken = default);
}

public sealed class MarketingMetaCampaignRow
{
    public Guid Id { get; init; }
    public string TenantId { get; init; } = string.Empty;
    public Guid JobId { get; init; }
    public string CampaignName { get; init; } = string.Empty;
    public string DestinationType { get; init; } = string.Empty;
    public string DestinationUrl { get; init; } = string.Empty;
    public string? WhatsappMessage { get; init; }
    public string AdText { get; init; } = string.Empty;
    public string ImageHash { get; init; } = string.Empty;
    public string? MetaCampaignId { get; init; }
    public string? MetaAdsetId { get; init; }
    public string? MetaCreativeId { get; init; }
    public string? MetaAdId { get; init; }
    public string Status { get; init; } = "PAUSED";
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}

public class MarketingMetaCampaignRepository : IMarketingMetaCampaignRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<MarketingMetaCampaignRepository> _logger;

    public MarketingMetaCampaignRepository(
        IDbConnectionFactory connectionFactory,
        ILogger<MarketingMetaCampaignRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        const string sql = @"
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'marketing_meta_campaigns' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.marketing_meta_campaigns
    (
        id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_marketing_meta_campaigns PRIMARY KEY,
        tenant_id NVARCHAR(100) NOT NULL,
        job_id UNIQUEIDENTIFIER NOT NULL,
        campaign_name NVARCHAR(250) NOT NULL,
        destination_type NVARCHAR(50) NOT NULL,
        destination_url NVARCHAR(MAX) NOT NULL,
        whatsapp_message NVARCHAR(MAX) NULL,
        ad_text NVARCHAR(MAX) NOT NULL,
        image_hash NVARCHAR(200) NOT NULL,
        meta_campaign_id NVARCHAR(100) NULL,
        meta_adset_id NVARCHAR(100) NULL,
        meta_creative_id NVARCHAR(100) NULL,
        meta_ad_id NVARCHAR(100) NULL,
        status NVARCHAR(50) NOT NULL CONSTRAINT DF_marketing_meta_campaigns_status DEFAULT ('PAUSED'),
        created_at_utc DATETIME2 NOT NULL CONSTRAINT DF_marketing_meta_campaigns_created DEFAULT (SYSUTCDATETIME()),
        updated_at_utc DATETIME2 NOT NULL CONSTRAINT DF_marketing_meta_campaigns_updated DEFAULT (SYSUTCDATETIME())
    );
END";

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    public async Task InsertAsync(MarketingMetaCampaignRow row, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        using var connection = _connectionFactory.CreateConnection();
        var databaseName = await connection.QuerySingleAsync<string>(
            new CommandDefinition("SELECT DB_NAME();", cancellationToken: cancellationToken));

        const string sql = @"
INSERT INTO dbo.marketing_meta_campaigns (
    id, tenant_id, job_id, campaign_name, destination_type, destination_url, whatsapp_message, ad_text, image_hash,
    meta_campaign_id, meta_adset_id, meta_creative_id, meta_ad_id, status, created_at_utc, updated_at_utc
) VALUES (
    @Id, @TenantId, @JobId, @CampaignName, @DestinationType, @DestinationUrl, @WhatsappMessage, @AdText, @ImageHash,
    @MetaCampaignId, @MetaAdsetId, @MetaCreativeId, @MetaAdId, @Status, SYSUTCDATETIME(), SYSUTCDATETIME()
);";

        var affected = await connection.ExecuteAsync(
            new CommandDefinition(sql, row, cancellationToken: cancellationToken));

        if (affected != 1)
        {
            throw new InvalidOperationException(
                $"marketing_meta_campaigns insert affected {affected} rows (expected 1) in database '{databaseName}'.");
        }

        _logger.LogInformation(
            "Inserted marketing_meta_campaigns row {LocalId} (Meta campaign {MetaCampaignId}) into database {Database}",
            row.Id,
            row.MetaCampaignId,
            databaseName);
    }

    public async Task<MarketingMetaCampaignRow?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT
    id AS Id,
    tenant_id AS TenantId,
    job_id AS JobId,
    campaign_name AS CampaignName,
    destination_type AS DestinationType,
    destination_url AS DestinationUrl,
    whatsapp_message AS WhatsappMessage,
    ad_text AS AdText,
    image_hash AS ImageHash,
    meta_campaign_id AS MetaCampaignId,
    meta_adset_id AS MetaAdsetId,
    meta_creative_id AS MetaCreativeId,
    meta_ad_id AS MetaAdId,
    status AS Status
FROM dbo.marketing_meta_campaigns
WHERE id = @id;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<MarketingMetaCampaignRow>(
            new CommandDefinition(sql, new { id }, cancellationToken: cancellationToken));
    }

    public async Task<MarketingMetaCampaignRow?> GetByMetaCampaignIdAsync(
        string metaCampaignId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT TOP 1
    id AS Id,
    tenant_id AS TenantId,
    job_id AS JobId,
    campaign_name AS CampaignName,
    destination_type AS DestinationType,
    destination_url AS DestinationUrl,
    whatsapp_message AS WhatsappMessage,
    ad_text AS AdText,
    image_hash AS ImageHash,
    meta_campaign_id AS MetaCampaignId,
    meta_adset_id AS MetaAdsetId,
    meta_creative_id AS MetaCreativeId,
    meta_ad_id AS MetaAdId,
    status AS Status
FROM dbo.marketing_meta_campaigns
WHERE meta_campaign_id = @metaCampaignId
ORDER BY created_at_utc DESC;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<MarketingMetaCampaignRow>(
            new CommandDefinition(
                sql,
                new { metaCampaignId = metaCampaignId.Trim() },
                cancellationToken: cancellationToken));
    }

    public async Task<MarketingMetaCampaignRow?> GetByMetaCampaignIdForTenantAsync(
        string metaCampaignId,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT TOP 1
    id AS Id,
    tenant_id AS TenantId,
    job_id AS JobId,
    campaign_name AS CampaignName,
    destination_type AS DestinationType,
    destination_url AS DestinationUrl,
    whatsapp_message AS WhatsappMessage,
    ad_text AS AdText,
    image_hash AS ImageHash,
    meta_campaign_id AS MetaCampaignId,
    meta_adset_id AS MetaAdsetId,
    meta_creative_id AS MetaCreativeId,
    meta_ad_id AS MetaAdId,
    status AS Status
FROM dbo.marketing_meta_campaigns
WHERE meta_campaign_id = @metaCampaignId AND tenant_id = @tenantId
ORDER BY created_at_utc DESC;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<MarketingMetaCampaignRow>(
            new CommandDefinition(
                sql,
                new { metaCampaignId = metaCampaignId.Trim(), tenantId },
                cancellationToken: cancellationToken));
    }

    public async Task<MarketingMetaCampaignRow?> GetLatestByJobIdForTenantAsync(
        Guid jobId,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT TOP 1
    id AS Id,
    tenant_id AS TenantId,
    job_id AS JobId,
    campaign_name AS CampaignName,
    destination_type AS DestinationType,
    destination_url AS DestinationUrl,
    whatsapp_message AS WhatsappMessage,
    ad_text AS AdText,
    image_hash AS ImageHash,
    meta_campaign_id AS MetaCampaignId,
    meta_adset_id AS MetaAdsetId,
    meta_creative_id AS MetaCreativeId,
    meta_ad_id AS MetaAdId,
    status AS Status
FROM dbo.marketing_meta_campaigns
WHERE job_id = @jobId AND tenant_id = @tenantId
ORDER BY created_at_utc DESC;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<MarketingMetaCampaignRow>(
            new CommandDefinition(sql, new { jobId, tenantId }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<MarketingMetaCampaignRow>> ListByTenantAsync(
        string tenantId,
        Guid? jobId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT
    id AS Id,
    tenant_id AS TenantId,
    job_id AS JobId,
    campaign_name AS CampaignName,
    destination_type AS DestinationType,
    destination_url AS DestinationUrl,
    whatsapp_message AS WhatsappMessage,
    ad_text AS AdText,
    image_hash AS ImageHash,
    meta_campaign_id AS MetaCampaignId,
    meta_adset_id AS MetaAdsetId,
    meta_creative_id AS MetaCreativeId,
    meta_ad_id AS MetaAdId,
    status AS Status,
    created_at_utc AS CreatedAtUtc,
    updated_at_utc AS UpdatedAtUtc
FROM dbo.marketing_meta_campaigns
WHERE tenant_id = @tenantId
  AND (@jobId IS NULL OR job_id = @jobId)
ORDER BY created_at_utc DESC;";

        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<MarketingMetaCampaignRow>(
            new CommandDefinition(sql, new { tenantId, jobId }, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    public async Task<MarketingMetaCampaignRow?> GetByIdForTenantAsync(
        Guid id,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT
    id AS Id,
    tenant_id AS TenantId,
    job_id AS JobId,
    campaign_name AS CampaignName,
    destination_type AS DestinationType,
    destination_url AS DestinationUrl,
    whatsapp_message AS WhatsappMessage,
    ad_text AS AdText,
    image_hash AS ImageHash,
    meta_campaign_id AS MetaCampaignId,
    meta_adset_id AS MetaAdsetId,
    meta_creative_id AS MetaCreativeId,
    meta_ad_id AS MetaAdId,
    status AS Status
FROM dbo.marketing_meta_campaigns
WHERE id = @id AND tenant_id = @tenantId;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<MarketingMetaCampaignRow>(
            new CommandDefinition(sql, new { id, tenantId }, cancellationToken: cancellationToken));
    }

    public async Task<bool> DeleteByIdForTenantAsync(
        Guid id,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
DELETE FROM dbo.marketing_meta_campaigns
WHERE id = @id AND tenant_id = @tenantId;";

        using var connection = _connectionFactory.CreateConnection();
        var n = await connection.ExecuteAsync(
            new CommandDefinition(sql, new { id, tenantId }, cancellationToken: cancellationToken));
        return n == 1;
    }

    public async Task<bool> DeleteByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        const string sql = @"DELETE FROM dbo.marketing_meta_campaigns WHERE id = @id;";

        using var connection = _connectionFactory.CreateConnection();
        var n = await connection.ExecuteAsync(
            new CommandDefinition(sql, new { id }, cancellationToken: cancellationToken));
        return n == 1;
    }

    public async Task<bool> UpdateStatusForTenantAsync(
        Guid id,
        string tenantId,
        string status,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
UPDATE dbo.marketing_meta_campaigns
SET status = @status, updated_at_utc = SYSUTCDATETIME()
WHERE id = @id AND tenant_id = @tenantId;";

        using var connection = _connectionFactory.CreateConnection();
        var n = await connection.ExecuteAsync(
            new CommandDefinition(sql, new { id, tenantId, status }, cancellationToken: cancellationToken));
        return n == 1;
    }

    public async Task<bool> UpdateStatusByIdAsync(
        Guid id,
        string status,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
UPDATE dbo.marketing_meta_campaigns
SET status = @status, updated_at_utc = SYSUTCDATETIME()
WHERE id = @id;";

        using var connection = _connectionFactory.CreateConnection();
        var n = await connection.ExecuteAsync(
            new CommandDefinition(sql, new { id, status }, cancellationToken: cancellationToken));
        return n == 1;
    }
}
