using Azure;
using Azure.AI.OpenAI;
using OpenAI.Chat;

namespace nexthire_api.Services;

public class WhatsAppAiService : IWhatsAppAiService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<WhatsAppAiService> _logger;

    public WhatsAppAiService(IConfiguration configuration, ILogger<WhatsAppAiService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string> GenerateReplyAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessage> messages,
        string? modelOverride,
        CancellationToken cancellationToken)
    {
        var endpoint = _configuration["AzureOpenAI:Endpoint"];
        var apiKey = _configuration["AzureOpenAI:ApiKey"];
        var deployment = modelOverride
            ?? _configuration["AzureOpenAI:Deployment"]
            ?? "gpt-4o-mini";

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("AzureOpenAI configuration is missing.");

        var client = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        var chatClient = client.GetChatClient(deployment);

        var chatMessages = new List<ChatMessage> { new SystemChatMessage(systemPrompt) };
        chatMessages.AddRange(messages);

        var response = await chatClient.CompleteChatAsync(
            chatMessages,
            new ChatCompletionOptions
            {
                MaxOutputTokenCount = 180,
                Temperature = 0.2f
            },
            cancellationToken);

        var content = response.Value.Content.FirstOrDefault()?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            _logger.LogWarning("Azure OpenAI returned empty content for WhatsApp reply.");
            return "Gracias por tu mensaje. Un reclutador te contactara pronto.";
        }

        return content;
    }
}
