using Dapper;
using nexthire_api.Data;
using nexthire_api.DTOs;
using nexthire_api.Helpers;

namespace nexthire_api.Repositories;

public class OrganizationSettingsRepository : IOrganizationSettingsRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public OrganizationSettingsRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<OrganizationSettingsDto?> GetAsync(Guid orgId)
    {
        const string sql = @"
            SELECT
                org_id AS OrgId,
                display_name AS DisplayName,
                website AS Website,
                contact_email AS ContactEmail,
                contact_phone AS ContactPhone,
                logo_base64 AS LogoBase64,
                logo_content_type AS LogoContentType,
                color_palette AS ColorPalette,
                created_at AS CreatedAt,
                updated_at AS UpdatedAt
            FROM organization_settings
            WHERE org_id = @orgId;";

        using var connection = _connectionFactory.CreateConnection();
        var row = await connection.QueryFirstOrDefaultAsync<OrganizationSettingsDto>(sql, new { orgId });
        return row is null ? null : Enrich(row);
    }

    public async Task<OrganizationSettingsDto> UpsertAsync(
        Guid orgId,
        string? displayName,
        string? website,
        string? contactEmail,
        string? contactPhone,
        string? logoBase64,
        string? logoContentType,
        string colorPalette)
    {
        const string sql = @"
            MERGE organization_settings AS target
            USING (SELECT @OrgId AS org_id) AS source
            ON target.org_id = source.org_id
            WHEN MATCHED THEN
                UPDATE SET
                    display_name = @DisplayName,
                    website = @Website,
                    contact_email = @ContactEmail,
                    contact_phone = @ContactPhone,
                    logo_base64 = @LogoBase64,
                    logo_content_type = @LogoContentType,
                    color_palette = @ColorPalette,
                    updated_at = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            WHEN NOT MATCHED THEN
                INSERT (org_id, display_name, website, contact_email, contact_phone, logo_base64, logo_content_type, color_palette, created_at, updated_at)
                VALUES (
                    @OrgId,
                    @DisplayName,
                    @Website,
                    @ContactEmail,
                    @ContactPhone,
                    @LogoBase64,
                    @LogoContentType,
                    @ColorPalette,
                    TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'),
                    TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
                )
            OUTPUT
                INSERTED.org_id AS OrgId,
                INSERTED.display_name AS DisplayName,
                INSERTED.website AS Website,
                INSERTED.contact_email AS ContactEmail,
                INSERTED.contact_phone AS ContactPhone,
                INSERTED.logo_base64 AS LogoBase64,
                INSERTED.logo_content_type AS LogoContentType,
                INSERTED.color_palette AS ColorPalette,
                INSERTED.created_at AS CreatedAt,
                INSERTED.updated_at AS UpdatedAt;";

        using var connection = _connectionFactory.CreateConnection();
        var row = await connection.QuerySingleAsync<OrganizationSettingsDto>(sql, new
        {
            OrgId = orgId,
            DisplayName = displayName,
            Website = website,
            ContactEmail = contactEmail,
            ContactPhone = contactPhone,
            LogoBase64 = logoBase64,
            LogoContentType = logoContentType,
            ColorPalette = colorPalette
        });

        return Enrich(row);
    }

    private static OrganizationSettingsDto Enrich(OrganizationSettingsDto row)
    {
        row.Palette = OrganizationColorPalettes.GetTokens(row.ColorPalette);
        return row;
    }
}
