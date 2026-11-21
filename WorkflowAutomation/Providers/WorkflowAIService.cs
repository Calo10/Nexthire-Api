using System.ClientModel;
using Azure;
using Azure.AI.OpenAI;
using OpenAI.Chat;
using Nexa.WorkflowAutomation.Contracts.Providers;
using Nexa.WorkflowAutomation.Serialization;

namespace nexthire_api.WorkflowAutomation.Providers;

/// <summary>
/// Workflow AI via Azure OpenAI (same config as AiAgentService).
/// </summary>
public sealed class WorkflowAIService : IAIService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<WorkflowAIService> _logger;

    public WorkflowAIService(IConfiguration configuration, ILogger<WorkflowAIService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<AIExecutionResult> ExecuteAsync(
        AIExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var apiKey = _configuration["AzureOpenAI:ApiKey"]
                ?? _configuration["AzureOpenAI:ChatApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return new AIExecutionResult
                {
                    Success = false,
                    ErrorCode = "ai.not_configured",
                    ErrorMessage = "Azure OpenAI is not configured.",
                };
            }

            var deployment = _configuration["AzureOpenAI:ChatDeployment"]
                ?? _configuration["AzureOpenAI:Deployment"]
                ?? "gpt-4o-mini";

            string? systemPrompt = null;
            double temperature = 0.4;
            int maxTokens = 800;
            if (request.Metadata is not null)
            {
                request.Metadata.TryGetValue("systemPrompt", out systemPrompt);
                if (request.Metadata.TryGetValue("temperature", out var tempRaw) &&
                    double.TryParse(tempRaw, out var temp))
                    temperature = temp;
                if (request.Metadata.TryGetValue("maxTokens", out var maxRaw) &&
                    int.TryParse(maxRaw, out var max))
                    maxTokens = max;
            }

            var client = new AzureOpenAIClient(ResolveUri(), new AzureKeyCredential(apiKey));
            var chat = client.GetChatClient(deployment);
            var messages = new List<ChatMessage>();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                messages.Add(new SystemChatMessage(systemPrompt));
            messages.Add(new UserChatMessage(request.Prompt ?? string.Empty));

            var response = await chat.CompleteChatAsync(
                messages,
                new ChatCompletionOptions
                {
                    MaxOutputTokenCount = Math.Clamp(maxTokens, 64, 4000),
                    Temperature = (float)Math.Clamp(temperature, 0, 1),
                },
                cancellationToken);

            var text = response.Value.Content.FirstOrDefault()?.Text?.Trim() ?? string.Empty;
            return new AIExecutionResult
            {
                Success = true,
                Output = WorkflowJsonSerializer.SerializeToElement(text),
                ExternalReferenceId = Guid.NewGuid().ToString("N"),
            };
        }
        catch (ClientResultException ex) when (ex.Status == 401)
        {
            _logger.LogWarning(ex, "Workflow AI rejected by Azure OpenAI");
            return new AIExecutionResult
            {
                Success = false,
                ErrorCode = "ai.unauthorized",
                ErrorMessage = "Azure OpenAI rejected the API key.",
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Workflow AI execution failed");
            return new AIExecutionResult
            {
                Success = false,
                ErrorCode = "ai.failed",
                ErrorMessage = ex.Message,
            };
        }
    }

    private Uri ResolveUri()
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

        throw new InvalidOperationException("Azure OpenAI is not configured.");
    }
}
