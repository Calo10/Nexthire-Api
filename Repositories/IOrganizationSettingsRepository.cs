using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public interface IOrganizationSettingsRepository
{
    Task<OrganizationSettingsDto?> GetAsync(Guid orgId);
    Task<OrganizationSettingsDto> UpsertAsync(
        Guid orgId,
        string? displayName,
        string? website,
        string? contactEmail,
        string? contactPhone,
        string? logoBase64,
        string? logoContentType,
        string colorPalette);
}
