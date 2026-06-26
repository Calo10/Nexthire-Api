using nexthire_api.DTOs;

namespace nexthire_api.Helpers;

public static class OrganizationColorPalettes
{
    public static IReadOnlyList<OrganizationColorPaletteOptionDto> ListOptions()
    {
        return All.Select(p => new OrganizationColorPaletteOptionDto
        {
            Id = p.Id,
            Name = p.Name,
            Description = p.Description,
            Tokens = p.Tokens
        }).ToList();
    }

    public static OrganizationColorPaletteTokensDto GetTokens(string? paletteId)
    {
        var normalized = NormalizeOrNull(paletteId);
        var match = All.FirstOrDefault(p => string.Equals(p.Id, normalized, StringComparison.OrdinalIgnoreCase));
        return match?.Tokens ?? All[0].Tokens;
    }

    public static string? NormalizeOrNull(string? paletteId)
    {
        if (string.IsNullOrWhiteSpace(paletteId))
            return null;

        var normalized = paletteId.Trim().ToLowerInvariant();
        return OrganizationColorPaletteCodes.All.Any(id =>
            string.Equals(id, normalized, StringComparison.OrdinalIgnoreCase))
            ? normalized
            : null;
    }

    private static readonly IReadOnlyList<PaletteDefinition> All =
    [
        new(
            OrganizationColorPaletteCodes.NexaDefault,
            "Nexa Default",
            "Azul marca Nexa, limpio y profesional.",
            Tokens(
                primary: "#2563EB",
                primaryForeground: "#FFFFFF",
                secondary: "#1E293B",
                accent: "#3B82F6",
                background: "#F8FAFC",
                surface: "#FFFFFF",
                text: "#0F172A",
                textMuted: "#64748B",
                border: "#E2E8F0",
                footerBackground: "#1E293B",
                footerText: "#FFFFFF",
                footerAccent: "#3B82F6")),
        new(
            OrganizationColorPaletteCodes.ProfessionalBlue,
            "Professional Blue",
            "Corporativo y confiable para empresas tradicionales.",
            Tokens(
                primary: "#1D4ED8",
                primaryForeground: "#FFFFFF",
                secondary: "#1E3A5F",
                accent: "#60A5FA",
                background: "#F1F5F9",
                surface: "#FFFFFF",
                text: "#0F172A",
                textMuted: "#475569",
                border: "#CBD5E1",
                footerBackground: "#1E3A5F",
                footerText: "#FFFFFF",
                footerAccent: "#60A5FA")),
        new(
            OrganizationColorPaletteCodes.ModernTeal,
            "Modern Teal",
            "Tech y startups, fresco y contemporáneo.",
            Tokens(
                primary: "#0D9488",
                primaryForeground: "#FFFFFF",
                secondary: "#134E4A",
                accent: "#2DD4BF",
                background: "#F0FDFA",
                surface: "#FFFFFF",
                text: "#042F2E",
                textMuted: "#0F766E",
                border: "#99F6E4",
                footerBackground: "#134E4A",
                footerText: "#FFFFFF",
                footerAccent: "#2DD4BF")),
        new(
            OrganizationColorPaletteCodes.WarmNeutral,
            "Warm Neutral",
            "Hospitalidad premium estilo NCA: página clara, footer navy oscuro y mint.",
            Tokens(
                primary: "#00D9A3",
                primaryForeground: "#FFFFFF",
                secondary: "#07243A",
                accent: "#00D9A3",
                background: "#F7FAFC",
                surface: "#FFFFFF",
                text: "#343A40",
                textMuted: "#6C757D",
                border: "#E9ECEF",
                footerBackground: "#021926",
                footerText: "#FFFFFF",
                footerAccent: "#00D9A3")),
        new(
            OrganizationColorPaletteCodes.GrowthGreen,
            "Growth Green",
            "Salud, sostenibilidad y bienestar.",
            Tokens(
                primary: "#15803D",
                primaryForeground: "#FFFFFF",
                secondary: "#14532D",
                accent: "#4ADE80",
                background: "#F0FDF4",
                surface: "#FFFFFF",
                text: "#052E16",
                textMuted: "#166534",
                border: "#BBF7D0",
                footerBackground: "#14532D",
                footerText: "#FFFFFF",
                footerAccent: "#4ADE80")),
        new(
            OrganizationColorPaletteCodes.BoldContrast,
            "Bold Contrast",
            "Alto contraste para alto volumen de vacantes.",
            Tokens(
                primary: "#DC2626",
                primaryForeground: "#FFFFFF",
                secondary: "#111827",
                accent: "#FACC15",
                background: "#FFFFFF",
                surface: "#F9FAFB",
                text: "#111827",
                textMuted: "#6B7280",
                border: "#D1D5DB",
                footerBackground: "#111827",
                footerText: "#FFFFFF",
                footerAccent: "#FACC15"))
    ];

    private static OrganizationColorPaletteTokensDto Tokens(
        string primary,
        string primaryForeground,
        string secondary,
        string accent,
        string background,
        string surface,
        string text,
        string textMuted,
        string border,
        string footerBackground,
        string footerText,
        string footerAccent) =>
        new()
        {
            Primary = primary,
            PrimaryForeground = primaryForeground,
            Secondary = secondary,
            Accent = accent,
            Background = background,
            Surface = surface,
            Text = text,
            TextMuted = textMuted,
            Border = border,
            FooterBackground = footerBackground,
            FooterText = footerText,
            FooterAccent = footerAccent
        };

    private sealed record PaletteDefinition(
        string Id,
        string Name,
        string Description,
        OrganizationColorPaletteTokensDto Tokens);
}
