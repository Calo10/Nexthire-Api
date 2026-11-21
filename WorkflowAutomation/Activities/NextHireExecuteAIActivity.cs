using System.Globalization;
using System.Text.Json;
using Nexa.WorkflowAutomation.Activities.AI;
using Nexa.WorkflowAutomation.Contracts.Activities;
using Nexa.WorkflowAutomation.Contracts.Providers;
using Nexa.WorkflowAutomation.Exceptions;
using Nexa.WorkflowAutomation.Serialization;
using Nexa.WorkflowAutomation.Utilities;

namespace nexthire_api.WorkflowAutomation.Activities;

public sealed class NextHireExecuteAIActivity : IWorkflowActivity
{
    private readonly ITemplateRenderer _templateRenderer;

    public NextHireExecuteAIActivity(ITemplateRenderer templateRenderer)
    {
        _templateRenderer = templateRenderer;
    }

    public string ActivityType => RunAIActivity.TypeName;

    public async Task<ActivityExecutionResult> ExecuteAsync(
        ActivityExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var configuration = context.Step.Configuration;

        var userPromptTemplate =
            ActivityConfigurationReader.GetOptionalString(configuration, "userPrompt")
            ?? ActivityConfigurationReader.GetOptionalString(configuration, "promptTemplate")
            ?? throw new ActivityConfigurationException(RunAIActivity.TypeName, "'userPrompt' is required.");

        var systemPromptTemplate = ActivityConfigurationReader.GetOptionalString(configuration, "systemPrompt");
        var responseVariable =
            ActivityConfigurationReader.GetOptionalString(configuration, "responseVariable")
            ?? ActivityConfigurationReader.GetOptionalString(configuration, "outputVariable")
            ?? "AI.Result";

        var variables = context.WorkflowContext.Variables;
        var systemPrompt = string.IsNullOrWhiteSpace(systemPromptTemplate)
            ? null
            : _templateRenderer.Render(systemPromptTemplate, variables);
        var userPrompt = _templateRenderer.Render(userPromptTemplate, variables);

        var ai = ActivityServiceLocator.GetRequiredService<IAIService>(context, RunAIActivity.TypeName);
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            metadata["systemPrompt"] = systemPrompt;
        if (ReadOptionalDouble(configuration, "temperature") is double temp)
            metadata["temperature"] = temp.ToString(CultureInfo.InvariantCulture);
        if (ActivityConfigurationReader.GetOptionalInt(configuration, "maxTokens") is int maxTokens)
            metadata["maxTokens"] = maxTokens.ToString(CultureInfo.InvariantCulture);

        var result = await ai.ExecuteAsync(new AIExecutionRequest
        {
            OrganizationId = context.Execution.OrganizationId,
            Operation = "complete",
            Prompt = userPrompt,
            Metadata = metadata,
        }, cancellationToken);

        if (!result.Success || result.Output is null)
        {
            return ActivityExecutionResult.Failed(
                result.ErrorCode ?? "ai.execute.failed",
                result.ErrorMessage ?? "AI execution failed.");
        }

        var outputVariable = responseVariable.Trim();
        var merged = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            [outputVariable] = JsonElementHelper.Clone(result.Output.Value)
        };

        var output = WorkflowJsonSerializer.SerializeToElement(new
        {
            responseVariable = outputVariable
        });

        return ActivityExecutionResult.Completed(output, merged);
    }

    private static double? ReadOptionalDouble(JsonElement configuration, string propertyName)
    {
        if (configuration.ValueKind != JsonValueKind.Object ||
            !configuration.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.GetDouble(),
            JsonValueKind.String when double.TryParse(
                property.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value) => value,
            _ => null
        };
    }
}
