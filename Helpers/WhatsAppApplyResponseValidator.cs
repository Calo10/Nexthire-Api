using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace nexthire_api.Helpers;

public static partial class WhatsAppApplyResponseValidator
{
    private static readonly string[] EnglishLevelTokens =
    [
        "basico", "básico", "basic", "elemental",
        "intermedio", "intermediate", "medio",
        "avanzado", "advanced", "alto",
        "nativo", "native", "bilingue", "bilingüe", "bilingual",
        "a1", "a2", "b1", "b2", "c1", "c2"
    ];

    public static bool LooksLikeQuestion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var value = text.Trim();
        if (value.EndsWith('?'))
            return true;

        var normalized = NormalizeForMatch(value);
        return QuestionPhraseRegex().IsMatch(normalized);
    }

    public static bool TryValidateName(string? text, out string? errorMessage)
    {
        errorMessage = null;
        var name = text?.Trim() ?? string.Empty;

        if (name.Length < 2)
        {
            errorMessage = "Por favor escribe tu nombre completo (al menos 2 caracteres).";
            return false;
        }

        if (LooksLikeQuestion(name))
        {
            errorMessage =
                "Lo usamos para identificarte en el proceso y que el reclutador sepa cómo dirigirse a ti. ¿Cuál es tu nombre completo?";
            return false;
        }

        if (!ContainsAtLeastTwoLetters(name))
        {
            errorMessage = "Escribe tu nombre completo usando letras (por ejemplo: Ana García).";
            return false;
        }

        if (QuestionPhraseRegex().IsMatch(NormalizeForMatch(name)))
        {
            errorMessage =
                "No parece un nombre. Escríbelo completo (por ejemplo: Ana García).";
            return false;
        }

        return true;
    }

    public static bool TryValidateEnglishLevel(string? text, out string? normalizedLevel, out string? errorMessage)
    {
        normalizedLevel = null;
        errorMessage = null;
        var level = text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(level))
        {
            errorMessage = "Indica tu nivel de inglés en una breve frase.";
            return false;
        }

        if (LooksLikeQuestion(level))
        {
            errorMessage =
                "El nivel de inglés ayuda a evaluar si cumples con los requisitos de la vacante. ¿Cuál es tu nivel? (básico, intermedio, avanzado, nativo)";
            return false;
        }

        var normalized = NormalizeForMatch(level);
        if (EnglishLevelTokens.Any(token => normalized.Contains(token, StringComparison.Ordinal)))
        {
            normalizedLevel = level;
            return true;
        }

        if (level.Length > 40 || !ContainsAtLeastTwoLetters(level))
        {
            errorMessage =
                "Indica tu nivel de inglés (por ejemplo: básico, intermedio, avanzado o nativo).";
            return false;
        }

        normalizedLevel = level;
        return true;
    }

    public static bool TryParseExperienceYears(string? text, out int years, out string? errorMessage)
    {
        years = 0;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            errorMessage = "Indica tus años de experiencia con un número (ejemplo: 5).";
            return false;
        }

        var value = text.Trim();
        if (LooksLikeQuestion(value))
        {
            errorMessage =
                "Los años de experiencia nos ayudan a ubicarte en el perfil que busca la empresa. Responde con un número (ejemplo: 5).";
            return false;
        }

        var match = ExperienceYearsRegex().Match(value);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out years))
        {
            errorMessage = "Indica tus años de experiencia con un número (ejemplo: 5).";
            return false;
        }

        if (years is < 0 or > 60)
        {
            errorMessage = "Indica un número de años entre 0 y 60 (ejemplo: 5).";
            years = 0;
            return false;
        }

        return true;
    }

    private static bool ContainsAtLeastTwoLetters(string value) =>
        value.Count(char.IsLetter) >= 2;

    private static string NormalizeForMatch(string value) =>
        value.ToLowerInvariant().Normalize(NormalizationForm.FormD);

    [GeneratedRegex(
        @"\b(por\s*que|porque|para\s*que|para\s*aque|por\s*aqu[eé]|y\s+eso|como\s+asi|how\s+come|what\s+for|why|ocupas\s+saber|tienes\s+que\s+saber|tenes\s+que\s+saber|necesitas\s+saber|para\s+que\s+saber|si\s+para)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuestionPhraseRegex();

    [GeneratedRegex(
        @"^\s*(\d{1,2})\s*(años|anos|years|year|a)?\s*[.!?]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExperienceYearsRegex();
}
