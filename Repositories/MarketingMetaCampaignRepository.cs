using Dapper;
using nexthire_api.Data;

namespace nexthire_api.Repositories;

public interface IMarketingMetaCampaignRepository
{
    Task InsertAsync(MarketingMetaCampaignRow row, CancellationToken cancellationToken = default);

    Task<MarketingMetaCampaignRow?> GetByIdForTenantAsync(Guid id, string tenantId, CancellationToken cancellationToken = default);

    Task<bool> DeleteByIdForTenantAsync(Guid id, string tenantId, CancellationToken cancellationToken = default);
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
}

public class MarketingMetaCampaignRepository : IMarketingMetaCampaignRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public MarketingMetaCampaignRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task InsertAsync(MarketingMetaCampaignRow row, CancellationToken cancellationToken = default)
    {
        const string sql = @"
INSERT INTO dbo.marketing_meta_campaigns (
    id, tenant_id, job_id, campaign_name, destination_type, destination_url, whatsapp_message, ad_text, image_hash,
    meta_campaign_id, meta_adset_id, meta_creative_id, meta_ad_id, status, created_at_utc, updated_at_utc
) VALUES (
    @Id, @TenantId, @JobId, @CampaignName, @DestinationType, @DestinationUrl, @WhatsappMessage, @AdText, @ImageHash,
    @MetaCampaignId, @MetaAdsetId, @MetaCreativeId, @MetaAdId, @Status, SYSUTCDATETIME(), SYSUTCDATETIME()
);";

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(
            new CommandDefinition(sql, row, cancellationToken: cancellationToken));
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
}
