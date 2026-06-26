using nexthire_api.DTOs;

namespace nexthire_api.Services;

public interface IOrganizationSettingsService
{
    Task<OrganizationSettingsDto> GetForOrgAsync(Guid orgId, CancellationToken cancellationToken = default);
    Task<OrganizationSettingsDto> UpsertForOrgAsync(
        Guid orgId,
        Guid nexaUserId,
        UpsertOrganizationSettingsRequestDto request,
        CancellationToken cancellationToken = default);
    Task<PublicOrganizationBrandingDto> GetPublicBrandingAsync(Guid orgId, CancellationToken cancellationToken = default);
    IReadOnlyList<OrganizationColorPaletteOptionDto> ListPaletteOptions();
}
