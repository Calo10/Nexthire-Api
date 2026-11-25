using System.Text.Json;
using nexthire_api.DTOs;
using nexthire_api.Repositories;
using nexthire_api.Services;
using Nexa.WorkflowAutomation.Contracts.Activities;
using Nexa.WorkflowAutomation.Exceptions;
using Nexa.WorkflowAutomation.Serialization;
using Nexa.WorkflowAutomation.Utilities;

namespace nexthire_api.WorkflowAutomation.Activities;

/// <summary>NextHire activity <c>nexthire.moveStage</c>.</summary>
public sealed class NextHireMoveStageActivity : IWorkflowActivity
{
    public const string TypeName = "nexthire.moveStage";

    private readonly IWorkflowVariableResolver _variableResolver;

    public NextHireMoveStageActivity(IWorkflowVariableResolver variableResolver)
    {
        _variableResolver = variableResolver;
    }

    public string ActivityType => TypeName;

    public async Task<ActivityExecutionResult> ExecuteAsync(
        ActivityExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var configuration = context.Step.Configuration;

        var applicationIdVariable =
            ActivityConfigurationReader.GetOptionalString(configuration, "applicationIdVariable")
            ?? "Application.Id";
        var stageIdVariable =
            ActivityConfigurationReader.GetOptionalString(configuration, "stageIdVariable")
            ?? "Application.TargetStageId";
        var stageName = ActivityConfigurationReader.GetOptionalString(configuration, "stageName");

        var applicationRaw = _variableResolver.ResolveAsString(context.WorkflowContext.Variables, applicationIdVariable)
            ?? throw new ActivityConfigurationException(TypeName, $"Variable '{applicationIdVariable}' is empty.");
        if (!Guid.TryParse(applicationRaw, out var applicationId) || applicationId == Guid.Empty)
            throw new ActivityConfigurationException(TypeName, $"Variable '{applicationIdVariable}' is not a GUID.");

        var applications = ActivityServiceLocator.GetRequiredService<IApplicationsService>(context, TypeName);
        var pipeline = ActivityServiceLocator.GetRequiredService<IPipelineRepository>(context, TypeName);

        Guid toStageId;
        var stageRaw = _variableResolver.ResolveAsString(context.WorkflowContext.Variables, stageIdVariable);
        if (Guid.TryParse(stageRaw, out var parsedStage) && parsedStage != Guid.Empty)
        {
            toStageId = parsedStage;
        }
        else if (!string.IsNullOrWhiteSpace(stageName))
        {
            var stages = await pipeline.EnsureDefaultStagesAsync(context.Execution.OrganizationId);
            var match = stages.FirstOrDefault(item =>
                string.Equals(item.Name, stageName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return ActivityExecutionResult.Failed("stage.not_found", $"Stage '{stageName}' was not found.");
            toStageId = match.Id;
        }
        else
        {
            throw new ActivityConfigurationException(
                TypeName,
                "Provide stageIdVariable or stageName.");
        }

        var moved = await applications.MoveStageAsync(
            context.Execution.OrganizationId,
            Guid.Empty,
            applicationId,
            new MoveStageRequestDto { ToStageId = toStageId });

        if (moved is null)
            return ActivityExecutionResult.Failed("application.not_found", "Application was not found.");

        var variables = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["Application.StageId"] = WorkflowJsonSerializer.SerializeToElement(toStageId.ToString("D")),
        };
        if (!string.IsNullOrWhiteSpace(moved.Application.CurrentStageName))
        {
            variables["Application.Stage"] =
                WorkflowJsonSerializer.SerializeToElement(moved.Application.CurrentStageName);
        }

        var output = WorkflowJsonSerializer.SerializeToElement(new
        {
            applicationId = applicationId.ToString("D"),
            stageId = toStageId.ToString("D"),
        });

        return ActivityExecutionResult.Completed(output, variables);
    }
}
