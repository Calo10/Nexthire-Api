using System.ComponentModel.DataAnnotations;
using nexthire_api.DTOs;
using nexthire_api.Helpers;
using nexthire_api.Repositories;

namespace nexthire_api.Services;

public class OrganizationSettingsService : IOrganizationSettingsService
{
    private readonly IOrganizationSettingsRepository _repository;
    private readonly IOrgMembershipAuthorizationService _orgAuth;

    public OrganizationSettingsService(
        IOrganizationSettingsRepository repository,
        IOrgMembershipAuthorizationService orgAuth)
    {
        _repository = repository;
        _orgAuth = orgAuth;
    }

    public async Task<OrganizationSettingsDto> GetForOrgAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var existing = await _repository.GetAsync(orgId);
        return existing ?? CreateDefaults(orgId);
    }

    public async Task<OrganizationSettingsDto> UpsertForOrgAsync(
        Guid orgId,
        Guid nexaUserId,
        UpsertOrganizationSettingsRequestDto request,
        CancellationToken cancellationToken = default)
    {
        await _orgAuth.EnsureOwnerOrAdminAsync(orgId, nexaUserId, cancellationToken);

        var palette = OrganizationColorPalettes.NormalizeOrNull(request.ColorPalette)
                      ?? throw new ArgumentException("colorPalette is invalid.");

        var displayName = NormalizeOptionalText(request.DisplayName, 120);
        var website = NormalizeWebsite(request.Website);
        var contactEmail = NormalizeContactEmail(request.ContactEmail);
        var contactPhone = NormalizeOptionalText(request.ContactPhone, 80);

        string? logoBase64;
        string? logoContentType;

        if (request.RemoveLogo)
        {
            logoBase64 = null;
            logoContentType = null;
        }
        else if (string.IsNullOrWhiteSpace(request.LogoBase64))
        {
            var existing = await _repository.GetAsync(orgId);
            logoBase64 = existing?.LogoBase64;
            logoContentType = existing?.LogoContentType;
        }
        else
        {
            logoBase64 = OrganizationLogoValidator.ValidateAndNormalize(
                request.LogoBase64,
                request.LogoContentType,
                removeLogo: false);

            logoContentType = request.LogoContentType?.Trim().ToLowerInvariant();
            if (logoContentType == "image/jpg")
                logoContentType = "image/jpeg";
        }

        if (logoBase64 is null)
            logoContentType = null;

        return await _repository.UpsertAsync(
            orgId,
            displayName,
            website,
            contactEmail,
            contactPhone,
            logoBase64,
            logoContentType,
            palette);
    }

    public async Task<PublicOrganizationBrandingDto> GetPublicBrandingAsync(
        Guid orgId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var settings = await _repository.GetAsync(orgId);
        if (settings is null)
        {
            var palette = OrganizationColorPaletteCodes.NexaDefault;
            return new PublicOrganizationBrandingDto
            {
                OrgId = orgId,
                ColorPalette = palette,
                Palette = OrganizationColorPalettes.GetTokens(palette)
            };
        }

        return new PublicOrganizationBrandingDto
        {
            OrgId = settings.OrgId,
            DisplayName = settings.DisplayName,
            Website = settings.Website,
            ContactEmail = settings.ContactEmail,
            ContactPhone = settings.ContactPhone,
            LogoBase64 = settings.LogoBase64,
            LogoContentType = settings.LogoContentType,
            ColorPalette = settings.ColorPalette,
            Palette = settings.Palette
        };
    }

    public IReadOnlyList<OrganizationColorPaletteOptionDto> ListPaletteOptions()
        => OrganizationColorPalettes.ListOptions();

    private static OrganizationSettingsDto CreateDefaults(Guid orgId)
    {
        var palette = OrganizationColorPaletteCodes.NexaDefault;
        return new OrganizationSettingsDto
        {
            OrgId = orgId,
            DisplayName = null,
            LogoBase64 = null,
            LogoContentType = null,
            ColorPalette = palette,
            Palette = OrganizationColorPalettes.GetTokens(palette),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static string? NormalizeOptionalText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            throw new ArgumentException($"Value exceeds the maximum length of {maxLength} characters.");

        return trimmed;
    }

    private static string? NormalizeWebsite(string? website)
    {
        var normalized = NormalizeOptionalText(website, 500);
        if (normalized is null)
            return null;

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("website must be a valid http or https URL.");
        }

        return normalized;
    }

    private static string? NormalizeContactEmail(string? email)
    {
        var normalized = NormalizeOptionalText(email, 320);
        if (normalized is null)
            return null;

        if (!new EmailAddressAttribute().IsValid(normalized))
            throw new ArgumentException("contactEmail is not a valid email address.");

        return normalized.ToLowerInvariant();
    }
}
