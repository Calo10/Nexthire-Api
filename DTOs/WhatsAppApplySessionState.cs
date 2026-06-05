namespace nexthire_api.DTOs;

/// <summary>
/// Persisted in whatsapp_conversations.bot_apply_session_json while collecting job-application fields.
/// </summary>
public class WhatsAppApplySessionState
{
    public string Step { get; set; } = WhatsAppApplySessionSteps.Idle;

    public string Language { get; set; } = JobLanguageCodes.Spanish;

    public int CurrentFixedFieldIndex { get; set; }

    public int CurrentQuestionIndex { get; set; }

    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? FullName { get; set; }
    public string? Email { get; set; }

    public List<WhatsAppApplySessionAnswer> Answers { get; set; } = [];
}

public class WhatsAppApplySessionAnswer
{
    public Guid QuestionId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTimeOffset AnsweredAtUtc { get; set; }
}

public static class WhatsAppApplySessionSteps
{
    public const string Idle = "idle";
    public const string FixedFields = "fixed_fields";
    public const string CustomQuestions = "custom_questions";
    public const string Done = "done";

    public static bool IsCollecting(string? step) =>
        string.Equals(step, FixedFields, StringComparison.OrdinalIgnoreCase)
        || string.Equals(step, CustomQuestions, StringComparison.OrdinalIgnoreCase);
}

public static class WhatsAppApplyFixedFieldKeys
{
    public const string FirstName = "first_name";
    public const string LastName = "last_name";
    public const string FullName = "full_name";
    public const string Email = "email";

    public static readonly string[] Order =
    [
        FirstName,
        LastName,
        Email
    ];
}
