using Dapper;
using nexthire_api.Data;

namespace nexthire_api.Repositories;

public interface IMarketingMetaInsightsSnapshotRepository
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(MarketingMetaInsightsSnapshotRow row, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DateOnly>> ListWeekStartsAsync(
        string tenantId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MarketingMetaInsightsSnapshotRow>> ListByWeekAsync(
        string tenantId,
        DateOnly weekStart,
        CancellationToken cancellationToken = default);
}

public sealed class MarketingMetaInsightsSnapshotRow
{
    public Guid Id { get; init; }
    public string TenantId { get; init; } = string.Empty;
    public DateTime SnapshotWeekStart { get; init; }
    public Guid? LocalCampaignId { get; init; }
    public string MetaCampaignId { get; init; } = string.Empty;
    public string CampaignName { get; init; } = string.Empty;
    public string Platform { get; init; } = "meta_ads";
    public string DatePreset { get; init; } = "maximum";
    public string? DateStart { get; init; }
    public string? DateStop { get; init; }
    public long? Impressions { get; init; }
    public long? Reach { get; init; }
    public long? Clicks { get; init; }
    public long? InlineLinkClicks { get; init; }
    public decimal? Spend { get; init; }
    public decimal? Cpc { get; init; }
    public decimal? Cpm { get; init; }
    public decimal? Ctr { get; init; }
    public long? MetaLeads { get; init; }
    public decimal? CostPerLead { get; init; }
    public int NexthireLeadsCount { get; init; }
    public decimal? CostPerCandidate { get; init; }
    public string Source { get; init; } = "insights_fetch";
    public DateTime CapturedAtUtc { get; init; }
}

public class MarketingMetaInsightsSnapshotRepository : IMarketingMetaInsightsSnapshotRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<MarketingMetaInsightsSnapshotRepository> _logger;

    public MarketingMetaInsightsSnapshotRepository(
        IDbConnectionFactory connectionFactory,
        ILogger<MarketingMetaInsightsSnapshotRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        const string sql = @"
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'marketing_meta_insights_snapshots' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.marketing_meta_insights_snapshots
    (
        id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_marketing_meta_insights_snapshots PRIMARY KEY,
        tenant_id NVARCHAR(100) NOT NULL,
        snapshot_week_start DATE NOT NULL,
        local_campaign_id UNIQUEIDENTIFIER NULL,
        meta_campaign_id NVARCHAR(100) NOT NULL,
        campaign_name NVARCHAR(250) NOT NULL,
        platform NVARCHAR(80) NOT NULL CONSTRAINT DF_mmis_platform DEFAULT ('meta_ads'),
        date_preset NVARCHAR(50) NOT NULL CONSTRAINT DF_mmis_date_preset DEFAULT ('maximum'),
        date_start NVARCHAR(40) NULL,
        date_stop NVARCHAR(40) NULL,
        impressions BIGINT NULL,
        reach BIGINT NULL,
        clicks BIGINT NULL,
        inline_link_clicks BIGINT NULL,
        spend DECIMAL(18,4) NULL,
        cpc DECIMAL(18,6) NULL,
        cpm DECIMAL(18,6) NULL,
        ctr DECIMAL(18,6) NULL,
        meta_leads BIGINT NULL,
        cost_per_lead DECIMAL(18,6) NULL,
        nexthire_leads_count INT NOT NULL CONSTRAINT DF_mmis_leads DEFAULT (0),
        cost_per_candidate DECIMAL(18,6) NULL,
        source NVARCHAR(40) NOT NULL CONSTRAINT DF_mmis_source DEFAULT ('insights_fetch'),
        captured_at_utc DATETIME2 NOT NULL CONSTRAINT DF_mmis_captured DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT UQ_mmis_tenant_meta_week UNIQUE (tenant_id, meta_campaign_id, snapshot_week_start)
    );
    CREATE INDEX IX_mmis_tenant_week ON dbo.marketing_meta_insights_snapshots (tenant_id, snapshot_week_start DESC);
END

IF COL_LENGTH('dbo.marketing_meta_insights_snapshots', 'platform') IS NULL
BEGIN
    ALTER TABLE dbo.marketing_meta_insights_snapshots ADD platform NVARCHAR(80) NOT NULL CONSTRAINT DF_mmis_platform DEFAULT ('meta_ads');
END";

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    public async Task UpsertAsync(MarketingMetaInsightsSnapshotRow row, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        const string sql = @"
MERGE dbo.marketing_meta_insights_snapshots AS target
USING (SELECT @TenantId AS tenant_id, @MetaCampaignId AS meta_campaign_id, @SnapshotWeekStart AS snapshot_week_start) AS source
ON target.tenant_id = source.tenant_id
   AND target.meta_campaign_id = source.meta_campaign_id
   AND target.snapshot_week_start = source.snapshot_week_start
WHEN MATCHED THEN
    UPDATE SET
        local_campaign_id = @LocalCampaignId,
        campaign_name = @CampaignName,
        platform = @Platform,
        date_preset = @DatePreset,
        date_start = @DateStart,
        date_stop = @DateStop,
        impressions = @Impressions,
        reach = @Reach,
        clicks = @Clicks,
        inline_link_clicks = @InlineLinkClicks,
        spend = @Spend,
        cpc = @Cpc,
        cpm = @Cpm,
        ctr = @Ctr,
        meta_leads = @MetaLeads,
        cost_per_lead = @CostPerLead,
        nexthire_leads_count = @NexthireLeadsCount,
        cost_per_candidate = @CostPerCandidate,
        source = @Source,
        captured_at_utc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (
        id, tenant_id, snapshot_week_start, local_campaign_id, meta_campaign_id, campaign_name,
        platform, date_preset, date_start, date_stop, impressions, reach, clicks, inline_link_clicks,
        spend, cpc, cpm, ctr, meta_leads, cost_per_lead, nexthire_leads_count, cost_per_candidate,
        source, captured_at_utc
    )
    VALUES (
        @Id, @TenantId, @SnapshotWeekStart, @LocalCampaignId, @MetaCampaignId, @CampaignName,
        @Platform, @DatePreset, @DateStart, @DateStop, @Impressions, @Reach, @Clicks, @InlineLinkClicks,
        @Spend, @Cpc, @Cpm, @Ctr, @MetaLeads, @CostPerLead, @NexthireLeadsCount, @CostPerCandidate,
        @Source, SYSUTCDATETIME()
    );";

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new
                {
                    row.Id,
                    row.TenantId,
                    SnapshotWeekStart = row.SnapshotWeekStart.Date,
                    row.LocalCampaignId,
                    row.MetaCampaignId,
                    row.CampaignName,
                    row.Platform,
                    row.DatePreset,
                    row.DateStart,
                    row.DateStop,
                    row.Impressions,
                    row.Reach,
                    row.Clicks,
                    row.InlineLinkClicks,
                    row.Spend,
                    row.Cpc,
                    row.Cpm,
                    row.Ctr,
                    row.MetaLeads,
                    row.CostPerLead,
                    row.NexthireLeadsCount,
                    row.CostPerCandidate,
                    row.Source
                },
                cancellationToken: cancellationToken));
        _logger.LogDebug(
            "Upserted insights snapshot tenant={TenantId} meta={MetaCampaignId} week={Week} source={Source}",
            row.TenantId,
            row.MetaCampaignId,
            row.SnapshotWeekStart,
            row.Source);
    }

    public async Task<IReadOnlyList<DateOnly>> ListWeekStartsAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        const string sql = @"
SELECT DISTINCT snapshot_week_start
FROM dbo.marketing_meta_insights_snapshots
WHERE tenant_id = @tenantId
ORDER BY snapshot_week_start DESC;";

        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<DateTime>(
            new CommandDefinition(sql, new { tenantId }, cancellationToken: cancellationToken));
        return rows.Select(d => DateOnly.FromDateTime(d)).ToList();
    }

    public async Task<IReadOnlyList<MarketingMetaInsightsSnapshotRow>> ListByWeekAsync(
        string tenantId,
        DateOnly weekStart,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        const string sql = @"
SELECT
    id AS Id,
    tenant_id AS TenantId,
    snapshot_week_start AS SnapshotWeekStart,
    local_campaign_id AS LocalCampaignId,
    meta_campaign_id AS MetaCampaignId,
    campaign_name AS CampaignName,
    platform AS Platform,
    date_preset AS DatePreset,
    date_start AS DateStart,
    date_stop AS DateStop,
    impressions AS Impressions,
    reach AS Reach,
    clicks AS Clicks,
    inline_link_clicks AS InlineLinkClicks,
    spend AS Spend,
    cpc AS Cpc,
    cpm AS Cpm,
    ctr AS Ctr,
    meta_leads AS MetaLeads,
    cost_per_lead AS CostPerLead,
    nexthire_leads_count AS NexthireLeadsCount,
    cost_per_candidate AS CostPerCandidate,
    source AS Source,
    captured_at_utc AS CapturedAtUtc
FROM dbo.marketing_meta_insights_snapshots
WHERE tenant_id = @tenantId AND snapshot_week_start = @weekStart
ORDER BY campaign_name ASC;";

        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<MarketingMetaInsightsSnapshotRow>(
            new CommandDefinition(
                sql,
                new { tenantId, weekStart = weekStart.ToDateTime(TimeOnly.MinValue) },
                cancellationToken: cancellationToken));
        return rows.ToList();
    }
}
