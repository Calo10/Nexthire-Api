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
        var apiKey = _configuration["AzureOpenAI:ApiKey"];
        var deployment = modelOverride
            ?? _configuration["AzureOpenAI:ChatDeployment"]
            ?? _configuration["AzureOpenAI:Deployment"]
            ?? "gpt-4o-mini";

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("AzureOpenAI configuration is missing.");

        var client = new AzureOpenAIClient(ResolveResourceUri(), new AzureKeyCredential(apiKey));
        var chatClient = client.GetChatClient(deployment);

        var chatMessages = new List<ChatMessage> { new SystemChatMessage(systemPrompt) };
        chatMessages.AddRange(messages);

        var response = await chatClient.CompleteChatAsync(
            chatMessages,
            new ChatCompletionOptions
            {
                MaxOutputTokenCount = 400,
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

    private Uri ResolveResourceUri()
    {
        var chatResource = _configuration["AzureOpenAI:ChatResource"]?.Trim();
        if (!string.IsNullOrEmpty(chatResource))
            return new Uri(chatResource.TrimEnd('/') + "/");

        var endpoint = _configuration["AzureOpenAI:Endpoint"]?.Trim();
        if (!string.IsNullOrEmpty(endpoint))
        {
            var uri = new Uri(endpoint);
            return new Uri($"{uri.Scheme}://{uri.Authority}/");
        }

        var resource = _configuration["AzureOpenAI:Resource"]?.Trim();
        if (!string.IsNullOrEmpty(resource))
            return new Uri(resource.TrimEnd('/') + "/");

        throw new InvalidOperationException("AzureOpenAI configuration is missing.");
    }
}
