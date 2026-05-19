using OpenAI.Chat;

namespace nexthire_api.Services;

public interface IWhatsAppAiService
{
    Task<string> GenerateReplyAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessage> messages,
        string? modelOverride,
        CancellationToken cancellationToken);
}
