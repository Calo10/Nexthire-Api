using System.ComponentModel.DataAnnotations;

namespace nexthire_api.DTOs;

public class OrganizationSettingsDto
{
    public Guid OrgId { get; set; }
    public string? DisplayName { get; set; }
    public string? Website { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? LogoBase64 { get; set; }
    public string? LogoContentType { get; set; }
    public string ColorPalette { get; set; } = OrganizationColorPaletteCodes.NexaDefault;
    public OrganizationColorPaletteTokensDto Palette { get; set; } = new();
    public bool FitScoringEnabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class PublicOrganizationBrandingDto
{
    public Guid OrgId { get; set; }
    public string? DisplayName { get; set; }
    public string? Website { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? LogoBase64 { get; set; }
    public string? LogoContentType { get; set; }
    public string ColorPalette { get; set; } = OrganizationColorPaletteCodes.NexaDefault;
    public OrganizationColorPaletteTokensDto Palette { get; set; } = new();
}

public class OrganizationColorPaletteOptionDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public OrganizationColorPaletteTokensDto Tokens { get; set; } = new();
}

public class OrganizationColorPaletteTokensDto
{
    public string Primary { get; set; } = string.Empty;
    public string PrimaryForeground { get; set; } = string.Empty;
    public string Secondary { get; set; } = string.Empty;
    public string Accent { get; set; } = string.Empty;
    public string Background { get; set; } = string.Empty;
    public string Surface { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string TextMuted { get; set; } = string.Empty;
    public string Border { get; set; } = string.Empty;
    public string FooterBackground { get; set; } = string.Empty;
    public string FooterText { get; set; } = string.Empty;
    public string FooterAccent { get; set; } = string.Empty;
}

public class UpsertOrganizationSettingsRequestDto
{
    [StringLength(120)]
    public string? DisplayName { get; set; }

    [StringLength(500)]
    public string? Website { get; set; }

    [EmailAddress]
    [StringLength(320)]
    public string? ContactEmail { get; set; }

    [StringLength(80)]
    public string? ContactPhone { get; set; }

    public string? LogoBase64 { get; set; }

    [StringLength(64)]
    public string? LogoContentType { get; set; }

    [Required]
    [StringLength(64)]
    public string ColorPalette { get; set; } = OrganizationColorPaletteCodes.NexaDefault;

    /// <summary>When true, removes the stored logo.</summary>
    public bool RemoveLogo { get; set; }

    public bool? FitScoringEnabled { get; set; }
}

public static class OrganizationColorPaletteCodes
{
    public const string NexaDefault = "nexa_default";
    public const string ProfessionalBlue = "professional_blue";
    public const string ModernTeal = "modern_teal";
    public const string WarmNeutral = "warm_neutral";
    public const string GrowthGreen = "growth_green";
    public const string BoldContrast = "bold_contrast";

    public static readonly IReadOnlyList<string> All =
    [
        NexaDefault,
        ProfessionalBlue,
        ModernTeal,
        WarmNeutral,
        GrowthGreen,
        BoldContrast
    ];
}
