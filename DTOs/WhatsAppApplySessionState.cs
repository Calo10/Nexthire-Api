namespace nexthire_api.DTOs;

/// <summary>
/// Persisted in whatsapp_conversations.bot_apply_session_json while collecting job-application fields.
/// </summary>
public class WhatsAppApplySessionState
{
    public string Step { get; set; } = WhatsAppApplySessionSteps.Idle;

    public string? FullName { get; set; }
    public string? EnglishLevel { get; set; }
    public int? ExperienceYears { get; set; }
    public string? ResumeUrl { get; set; }
}

public static class WhatsAppApplySessionSteps
{
    public const string Idle = "idle";
    public const string Name = "name";
    public const string English = "english";
    public const string Experience = "experience";
    public const string Resume = "resume";
    public const string Done = "done";

    public static bool IsCollecting(string? step) =>
        step is Name or English or Experience or Resume;
}
