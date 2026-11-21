using System.Text.Json;
using nexthire_api.Models.Tasks;
using nexthire_api.Repositories;
using Nexa.WorkflowAutomation.Contracts.Activities;
using Nexa.WorkflowAutomation.Exceptions;
using Nexa.WorkflowAutomation.Serialization;
using Nexa.WorkflowAutomation.Utilities;

namespace nexthire_api.WorkflowAutomation.Activities;

public sealed class NextHireCreateTaskActivity : IWorkflowActivity
{
    public const string TypeName = "tasks.create";

    private readonly ITemplateRenderer _templateRenderer;
    private readonly IWorkflowVariableResolver _variableResolver;

    public NextHireCreateTaskActivity(
        ITemplateRenderer templateRenderer,
        IWorkflowVariableResolver variableResolver)
    {
        _templateRenderer = templateRenderer;
        _variableResolver = variableResolver;
    }

    public string ActivityType => TypeName;

    public async Task<ActivityExecutionResult> ExecuteAsync(
        ActivityExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var configuration = context.Step.Configuration;

        var titleTemplate =
            ActivityConfigurationReader.GetOptionalString(configuration, "titleTemplate")
            ?? ActivityConfigurationReader.GetRequiredString(TypeName, configuration, "title");

        var title = _templateRenderer.Render(titleTemplate, context.WorkflowContext.Variables).Trim();
        if (title.Length == 0)
            throw new ActivityConfigurationException(TypeName, "Task title is empty after template render.");

        var applicationRaw = _variableResolver.ResolveAsString(context.WorkflowContext.Variables, "Application.Id");
        if (!Guid.TryParse(applicationRaw, out var applicationId) || applicationId == Guid.Empty)
            throw new ActivityConfigurationException(TypeName, "Application.Id is required to create a task.");

        var tasks = ActivityServiceLocator.GetRequiredService<ITasksRepository>(context, TypeName);
        var id = Guid.NewGuid();
        var created = await tasks.CreateAsync(
            context.Execution.OrganizationId,
            id,
            new CreateTaskRequest
            {
                Title = title.Length > 400 ? title[..400] : title,
                ApplicationId = applicationId,
                Status = "todo",
            },
            "todo");

        if (created is null)
            return ActivityExecutionResult.Failed("task.create.failed", "Could not create task.");

        var variables = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["Task.Id"] = WorkflowJsonSerializer.SerializeToElement(created.Id.ToString("D")),
        };

        var output = WorkflowJsonSerializer.SerializeToElement(new
        {
            taskId = created.Id.ToString("D"),
        });

        return ActivityExecutionResult.Completed(output, variables);
    }
}
