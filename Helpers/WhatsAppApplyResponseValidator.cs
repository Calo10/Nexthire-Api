using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using nexthire_api.DTOs;

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

    public static bool TryValidateName(string? text, string language, out string? errorMessage)
    {
        errorMessage = null;
        var name = text?.Trim() ?? string.Empty;

        if (name.Length < 2)
        {
            errorMessage = WhatsAppBotMessages.FullNameInvalid(language);
            return false;
        }

        if (LooksLikeQuestion(name))
        {
            errorMessage = WhatsAppBotMessages.FullNameQuestion(language);
            return false;
        }

        if (!ContainsAtLeastTwoLetters(name))
        {
            errorMessage = WhatsAppBotMessages.FullNameLettersExample(language);
            return false;
        }

        if (QuestionPhraseRegex().IsMatch(NormalizeForMatch(name)))
        {
            errorMessage = WhatsAppBotMessages.FullNameDoesNotLookValid(language);
            return false;
        }

        return true;
    }

    public static bool TryValidateFixedField(
        string fieldKey,
        string? text,
        string language,
        out string? normalizedValue,
        out string? errorMessage)
    {
        normalizedValue = null;
        errorMessage = null;

        return fieldKey switch
        {
            WhatsAppApplyFixedFieldKeys.FirstName => TryValidatePersonName(
                text,
                WhatsAppBotMessages.FieldLabelFirstName(language),
                language,
                out normalizedValue,
                out errorMessage),
            WhatsAppApplyFixedFieldKeys.LastName => TryValidatePersonName(
                text,
                WhatsAppBotMessages.FieldLabelLastName(language),
                language,
                out normalizedValue,
                out errorMessage),
            WhatsAppApplyFixedFieldKeys.Email => TryValidateEmailField(text, language, out normalizedValue, out errorMessage),
            _ => Fail(WhatsAppBotMessages.UnsupportedAnswerType(language, fieldKey), out normalizedValue, out errorMessage)
        };
    }

    private static bool TryValidatePersonName(
        string? text,
        string fieldLabel,
        string language,
        out string? normalizedValue,
        out string? errorMessage)
    {
        var value = text?.Trim() ?? string.Empty;
        if (value.Length < 2)
        {
            errorMessage = WhatsAppBotMessages.PersonNameTooShort(language, fieldLabel);
            normalizedValue = null;
            return false;
        }

        if (LooksLikeQuestion(value))
        {
            errorMessage = WhatsAppBotMessages.PersonNameQuestion(language, fieldLabel);
            normalizedValue = null;
            return false;
        }

        if (!ContainsAtLeastTwoLetters(value))
        {
            errorMessage = WhatsAppBotMessages.PersonNameLettersOnly(language, fieldLabel);
            normalizedValue = null;
            return false;
        }

        return Assign(value, out normalizedValue, out errorMessage);
    }

    private static bool TryValidateFullNameField(
        string? text,
        string language,
        out string? normalizedValue,
        out string? errorMessage)
    {
        if (!TryValidateName(text, language, out errorMessage))
        {
            normalizedValue = null;
            return false;
        }

        return Assign(text!.Trim(), out normalizedValue, out errorMessage);
    }

    private static bool TryValidateEmailField(
        string? text,
        string language,
        out string? normalizedValue,
        out string? errorMessage)
    {
        var value = text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            errorMessage = WhatsAppBotMessages.EmailRequired(language);
            normalizedValue = null;
            return false;
        }

        if (LooksLikeQuestion(value))
        {
            errorMessage = WhatsAppBotMessages.EmailQuestion(language);
            normalizedValue = null;
            return false;
        }

        if (!EmailRegex().IsMatch(value))
        {
            errorMessage = WhatsAppBotMessages.EmailInvalid(language);
            normalizedValue = null;
            return false;
        }

        return Assign(value.ToLowerInvariant(), out normalizedValue, out errorMessage);
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

    public static bool TryValidateAnswer(
        JobBotQuestionDto question,
        WhatsAppInboundMessageDto inbound,
        string language,
        out string? normalizedValue,
        out string? errorMessage)
    {
        normalizedValue = null;
        errorMessage = null;

        return question.AnswerType switch
        {
            JobBotQuestionAnswerTypes.Text => TryValidateTextAnswer(question, inbound.Body, language, out normalizedValue, out errorMessage),
            JobBotQuestionAnswerTypes.Number => TryValidateNumberAnswer(question, inbound.Body, language, out normalizedValue, out errorMessage),
            JobBotQuestionAnswerTypes.YesNo => TryValidateYesNoAnswer(question, inbound.Body, language, out normalizedValue, out errorMessage),
            JobBotQuestionAnswerTypes.File => TryValidateFileAnswer(question, inbound, language, out normalizedValue, out errorMessage),
            _ => Fail(WhatsAppBotMessages.UnsupportedAnswerType(language, question.QuestionKey), out normalizedValue, out errorMessage)
        };
    }

    private static bool TryValidateTextAnswer(
        JobBotQuestionDto question,
        string? text,
        string language,
        out string? normalizedValue,
        out string? errorMessage)
    {
        if (IsFullNameQuestion(question.QuestionKey))
            return TryValidateName(text, language, out errorMessage)
                ? Assign(text?.Trim(), out normalizedValue, out errorMessage)
                : Fail(errorMessage!, out normalizedValue, out errorMessage);

        var value = text?.Trim() ?? string.Empty;
        if (value.Length < 1)
            return Fail(WhatsAppBotMessages.TextAnswerRequired(language), out normalizedValue, out errorMessage);

        if (LooksLikeQuestion(value))
            return Fail(
                WhatsAppBotMessages.TextAnswerQuestion(language, question.QuestionText),
                out normalizedValue,
                out errorMessage);

        return Assign(value, out normalizedValue, out errorMessage);
    }

    private static bool TryValidateNumberAnswer(
        JobBotQuestionDto question,
        string? text,
        string language,
        out string? normalizedValue,
        out string? errorMessage)
    {
        if (!TryParseExperienceYears(text, out var years, out var parseError))
        {
            errorMessage = LooksLikeQuestion(text)
                ? WhatsAppBotMessages.NumberQuestion(language, question.QuestionText)
                : parseError ?? WhatsAppBotMessages.NumberInvalid(language);
            normalizedValue = null;
            return false;
        }

        return Assign(years.ToString(CultureInfo.InvariantCulture), out normalizedValue, out errorMessage);
    }

    private static bool TryValidateYesNoAnswer(
        JobBotQuestionDto question,
        string? text,
        string language,
        out string? normalizedValue,
        out string? errorMessage)
    {
        var value = text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return Fail(WhatsAppBotMessages.YesNoRequired(language), out normalizedValue, out errorMessage);

        if (LooksLikeQuestion(value))
            return Fail(
                WhatsAppBotMessages.TextAnswerQuestion(language, question.QuestionText),
                out normalizedValue,
                out errorMessage);

        var normalized = NormalizeForMatch(value);
        if (YesRegex().IsMatch(normalized))
            return Assign("yes", out normalizedValue, out errorMessage);

        if (NoRegex().IsMatch(normalized))
            return Assign("no", out normalizedValue, out errorMessage);

        return Fail(WhatsAppBotMessages.YesNoRequired(language), out normalizedValue, out errorMessage);
    }

    private static bool TryValidateFileAnswer(
        JobBotQuestionDto question,
        WhatsAppInboundMessageDto inbound,
        string language,
        out string? normalizedValue,
        out string? errorMessage)
    {
        var url = WhatsAppDynamicApplyHelper.TryGetFileAnswerUrl(inbound);
        if (string.IsNullOrWhiteSpace(url))
        {
            errorMessage = LooksLikeQuestion(inbound.Body)
                ? WhatsAppBotMessages.FileQuestion(language, question.QuestionText)
                : WhatsAppBotMessages.FileRequired(language);
            normalizedValue = null;
            return false;
        }

        return Assign(url, out normalizedValue, out errorMessage);
    }

    private static bool IsFullNameQuestion(string questionKey) =>
        questionKey.Equals("full_name", StringComparison.OrdinalIgnoreCase)
        || questionKey.Equals("nombre", StringComparison.OrdinalIgnoreCase)
        || questionKey.Equals("nombre_completo", StringComparison.OrdinalIgnoreCase)
        || questionKey.Equals("name", StringComparison.OrdinalIgnoreCase);

    private static bool Assign(string? value, out string? normalizedValue, out string? errorMessage)
    {
        normalizedValue = value;
        errorMessage = null;
        return true;
    }

    private static bool Fail(string message, out string? normalizedValue, out string? errorMessage)
    {
        normalizedValue = null;
        errorMessage = message;
        return false;
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

    [GeneratedRegex(@"^(si|sí|yes|y|s)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex YesRegex();

    [GeneratedRegex(@"^(no|n)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoRegex();

    [GeneratedRegex(
        @"^[^\s@]+@[^\s@]+\.[^\s@]+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();
}
