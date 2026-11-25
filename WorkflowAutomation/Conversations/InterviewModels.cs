using System.Text.Json;

namespace nexthire_api.WorkflowAutomation.Conversations;

public sealed class InterviewQuestionDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Variable { get; set; } = string.Empty;
}

public sealed class InterviewSession
{
    public string ConversationId { get; set; } = string.Empty;
    public Guid OrganizationId { get; set; }
    public Guid WorkflowExecutionId { get; set; }
    public string Channel { get; set; } = "whatsapp";
    public string Recipient { get; set; } = string.Empty;
    public int QuestionIndex { get; set; }
    public Dictionary<string, string> Answers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<InterviewQuestionDefinition> Questions { get; set; } =
        Array.Empty<InterviewQuestionDefinition>();
    public string CompletionEventType { get; set; } = "conversation.completed";
    public string Status { get; set; } = InterviewSessionStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class InterviewSessionStatus
{
    public const string Active = "Active";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
}

public sealed class StartInterviewRequest
{
    public Guid OrganizationId { get; set; }
    public Guid WorkflowExecutionId { get; set; }
    public required string Channel { get; set; }
    public required string Recipient { get; set; }
    public string? IntroMessage { get; set; }
    public required IReadOnlyList<InterviewQuestionDefinition> Questions { get; set; }
    public string CompletionEventType { get; set; } = "conversation.completed";
}

public sealed class StartInterviewResult
{
    public bool Success { get; set; }
    public string? ConversationId { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class ProcessInterviewReplyResult
{
    public bool Handled { get; set; }
    public bool Completed { get; set; }
    public Guid? OrganizationId { get; set; }
    public string? Recipient { get; set; }
    public string? ConversationId { get; set; }
    public Guid? WorkflowExecutionId { get; set; }
    public string? CompletionEventType { get; set; }
    public IReadOnlyDictionary<string, string>? Answers { get; set; }
}
