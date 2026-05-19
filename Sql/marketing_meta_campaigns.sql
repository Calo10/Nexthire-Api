-- Meta Ads campaign records (NextHire Marketing)
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'marketing_meta_campaigns' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.marketing_meta_campaigns
    (
        id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_marketing_meta_campaigns PRIMARY KEY DEFAULT NEWID(),
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
END
GO
