using nexthire_api.DTOs;

namespace nexthire_api.Helpers;

public static class WhatsAppBotMessages
{
    public static string NormalizeLanguage(string? value) =>
        string.Equals(value, JobLanguageCodes.English, StringComparison.OrdinalIgnoreCase)
            ? JobLanguageCodes.English
            : JobLanguageCodes.Spanish;

    public static bool IsEnglish(string language) =>
        string.Equals(NormalizeLanguage(language), JobLanguageCodes.English, StringComparison.Ordinal);

    public static string InvalidJobId(string language) =>
        IsEnglish(language)
            ? "The job id is not valid. It must be a GUID with hexadecimal characters only, as shown in the job link. If you copied the text, check that no characters are missing or extra."
            : "El identificador de la vacante no es válido. Debe ser un GUID con solo números y letras A a F (hexadecimal), como en el enlace del aviso. Si copiaste el texto, revisa que no falte ni sobre ningún carácter.";

    public static string JobNotFound(string language) =>
        IsEnglish(language)
            ? "We could not find that job. Check the job link and try again."
            : "No encontramos esa vacante. Verifica el enlace del aviso e inténtalo de nuevo.";

    public static string SaveApplicationError(string language) =>
        IsEnglish(language)
            ? "There was a problem saving your application. Please try again in a few minutes."
            : "Hubo un problema al guardar tu postulación. Por favor intenta de nuevo en unos minutos.";

    public static string ApplicationComplete(string language) =>
        IsEnglish(language)
            ? "Done! We registered your application. A recruiter will review your information soon."
            : "¡Listo! Registramos tu postulación. Un reclutador revisará tu información pronto.";

    public static string ContinueApplication(string language) =>
        IsEnglish(language) ? "Let's continue with your application." : "Seguimos con tu postulación.";

    public static string ContinueWithQuestion(string language, string questionText) =>
        $"{ContinueApplication(language)} {questionText.Trim()}";

    public static string FixedFieldPrompt(string language, string fieldKey, bool isFirstQuestion)
    {
        var question = fieldKey switch
        {
            WhatsAppApplyFixedFieldKeys.FirstName => IsEnglish(language) ? "What is your first name?" : "¿Cuál es tu nombre?",
            WhatsAppApplyFixedFieldKeys.LastName => IsEnglish(language) ? "What is your last name?" : "¿Cuál es tu apellido?",
            WhatsAppApplyFixedFieldKeys.Email => IsEnglish(language) ? "What is your email address?" : "¿Cuál es tu correo electrónico?",
            _ => ContinueApplication(language)
        };

        if (isFirstQuestion)
        {
            return IsEnglish(language)
                ? $"Hi! I see you want to apply for this job. {question}"
                : $"¡Hola! Veo que quieres aplicar a esta vacante. {question}";
        }

        return question;
    }

    public static string FixedFieldRepeatPrompt(string language, string fieldKey) =>
        ContinueWithQuestion(language, fieldKey switch
        {
            WhatsAppApplyFixedFieldKeys.FirstName => IsEnglish(language) ? "What is your first name?" : "¿Cuál es tu nombre?",
            WhatsAppApplyFixedFieldKeys.LastName => IsEnglish(language) ? "What is your last name?" : "¿Cuál es tu apellido?",
            WhatsAppApplyFixedFieldKeys.Email => IsEnglish(language) ? "What is your email address?" : "¿Cuál es tu correo electrónico?",
            _ => ContinueApplication(language)
        });

    public static string PersonNameTooShort(string language, string fieldLabel) =>
        IsEnglish(language)
            ? $"Please enter your {fieldLabel} (at least 2 characters)."
            : $"Por favor escribe tu {fieldLabel} (al menos 2 caracteres).";

    public static string PersonNameQuestion(string language, string fieldLabel) =>
        IsEnglish(language)
            ? $"We need your {fieldLabel} to identify you in the process. What is your {fieldLabel}?"
            : $"Necesitamos tu {fieldLabel} para identificarte en el proceso. ¿Cuál es tu {fieldLabel}?";

    public static string PersonNameLettersOnly(string language, string fieldLabel) =>
        IsEnglish(language)
            ? $"Enter your {fieldLabel} using letters."
            : $"Escribe tu {fieldLabel} usando letras.";

    public static string FullNameInvalid(string language) =>
        IsEnglish(language)
            ? "Please enter your full name (at least 2 characters)."
            : "Por favor escribe tu nombre completo (al menos 2 caracteres).";

    public static string FullNameQuestion(string language) =>
        IsEnglish(language)
            ? "We use it to identify you and so the recruiter knows how to address you. What is your full name?"
            : "Lo usamos para identificarte en el proceso y que el reclutador sepa cómo dirigirse a ti. ¿Cuál es tu nombre completo?";

    public static string FullNameLettersExample(string language) =>
        IsEnglish(language)
            ? "Enter your full name using letters (for example: Ana Garcia)."
            : "Escribe tu nombre completo usando letras (por ejemplo: Ana García).";

    public static string FullNameDoesNotLookValid(string language) =>
        IsEnglish(language)
            ? "That does not look like a name. Enter your full name (for example: Ana Garcia)."
            : "No parece un nombre. Escríbelo completo (por ejemplo: Ana García).";

    public static string EmailRequired(string language) =>
        IsEnglish(language)
            ? "Please enter your email address."
            : "Por favor escribe tu correo electrónico.";

    public static string EmailQuestion(string language) =>
        IsEnglish(language)
            ? "We need your email to contact you about this job. What is your email address?"
            : "Necesitamos tu correo para contactarte sobre la vacante. ¿Cuál es tu correo electrónico?";

    public static string EmailInvalid(string language) =>
        IsEnglish(language)
            ? "Enter a valid email address (for example: ana@email.com)."
            : "Escribe un correo electrónico válido (por ejemplo: ana@correo.com).";

    public static string TextAnswerRequired(string language) =>
        IsEnglish(language) ? "Please enter an answer." : "Por favor escribe una respuesta.";

    public static string TextAnswerQuestion(string language, string questionText) =>
        IsEnglish(language)
            ? $"We need this information to evaluate your application. {questionText.Trim()}"
            : $"Necesitamos esta información para evaluar tu postulación. {questionText.Trim()}";

    public static string NumberInvalid(string language) =>
        IsEnglish(language) ? "Enter a valid number." : "Indica un número válido.";

    public static string NumberQuestion(string language, string questionText) =>
        IsEnglish(language)
            ? $"This information helps us evaluate your profile for the job. {questionText.Trim()}"
            : $"Esta información nos ayuda a evaluar tu perfil para la vacante. {questionText.Trim()}";

    public static string YesNoRequired(string language) =>
        IsEnglish(language) ? "Please answer yes or no." : "Responde sí o no.";

    public static string FileRequired(string language) =>
        IsEnglish(language)
            ? "Please send your resume as a PDF or Word file."
            : "Por favor envía tu CV en PDF o Word.";

    public static string ResumeUploadError(string language, Exception? cause = null)
    {
        var failureCode = cause is null ? "unknown" : WhatsAppResumeUploadDiagnostics.ClassifyUploadFailure(cause);

        return failureCode switch
        {
            "twilio_credentials_missing" or "twilio_download_failed" => IsEnglish(language)
                ? "We could not retrieve your file from WhatsApp. Please try again in a few minutes."
                : "No pudimos obtener tu archivo de WhatsApp. Intenta de nuevo en unos minutos.",
            "documents_wrong_host_or_not_running" or "documents_upload_failed" or "documents_unauthorized" => IsEnglish(language)
                ? "We received your file but could not save it. Please try again in a few minutes."
                : "Recibimos tu archivo pero no pudimos guardarlo. Intenta de nuevo en unos minutos.",
            "invalid_file_type" => IsEnglish(language)
                ? "Please send your resume as a PDF or Word file."
                : "Por favor envía tu CV en PDF o Word.",
            "file_too_large" => IsEnglish(language)
                ? "Your file is too large. Please send a PDF or Word file under 10 MB."
                : "Tu archivo es muy grande. Envía un PDF o Word de menos de 10 MB.",
            _ => IsEnglish(language)
                ? "We could not process your file. Please send a PDF or Word document (max 10 MB) and try again."
                : "No pudimos procesar tu archivo. Envía un PDF o Word (máx. 10 MB) e inténtalo de nuevo."
        };
    }

    public static string FileQuestion(string language, string questionText) =>
        IsEnglish(language)
            ? $"We need this file to complete your application. {questionText.Trim()}"
            : $"Necesitamos este archivo para completar tu postulación. {questionText.Trim()}";

    public static string UnsupportedAnswerType(string language, string questionKey) =>
        IsEnglish(language)
            ? $"Unsupported answer type for question '{questionKey}'."
            : $"Tipo de respuesta no soportado para la pregunta '{questionKey}'.";

    public static string FieldLabelFirstName(string language) =>
        IsEnglish(language) ? "first name" : "nombre";

    public static string FieldLabelLastName(string language) =>
        IsEnglish(language) ? "last name" : "apellido";
}
