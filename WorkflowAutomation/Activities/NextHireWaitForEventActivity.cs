using Nexa.WorkflowAutomation.Activities.Flow;
using Nexa.WorkflowAutomation.Contracts.Activities;
using Nexa.WorkflowAutomation.Exceptions;
using Nexa.WorkflowAutomation.Utilities;

namespace nexthire_api.WorkflowAutomation.Activities;

/// <summary>
/// Reads UI aliases (<c>correlationKey</c>, <c>timeout</c>) and falls back to the email conversation id
/// so wait-for-reply matches inbound org + prospect email, like WhatsApp org + phone.
/// </summary>
public sealed class NextHireWaitForEventActivity : IWorkflowActivity
{
    private readonly IWorkflowVariableResolver _variableResolver;

    public NextHireWaitForEventActivity(IWorkflowVariableResolver variableResolver)
    {
        _variableResolver = variableResolver ?? throw new ArgumentNullException(nameof(variableResolver));
    }

    public string ActivityType => WaitForEventActivity.TypeName;

    public Task<ActivityExecutionResult> ExecuteAsync(
        ActivityExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var configuration = context.Step.Configuration;
        var eventType = ActivityConfigurationReader.GetRequiredString(
            WaitForEventActivity.TypeName, configuration, "eventType");
        var correlationIdVariable =
            ActivityConfigurationReader.GetOptionalString(configuration, "correlationIdVariable")
            ?? ActivityConfigurationReader.GetOptionalString(configuration, "correlationKey");

        string? correlationId = null;
        if (!string.IsNullOrWhiteSpace(correlationIdVariable))
        {
            correlationId = _variableResolver.ResolveAsString(
                context.WorkflowContext.Variables,
                correlationIdVariable);
            if (string.IsNullOrWhiteSpace(correlationId))
                correlationId = correlationIdVariable.Trim();
        }

        if (string.IsNullOrWhiteSpace(correlationId))
            correlationId = context.Execution.ConversationId ?? context.WorkflowContext.ConversationId;

        var email = EmailTenantResolver.NormalizeEmail(correlationId);
        if (!string.IsNullOrWhiteSpace(email))
        {
            correlationId = EmailTenantResolver.BuildConversationId(
                context.Execution.OrganizationId, email);
        }
        else if (eventType.StartsWith("email.", StringComparison.OrdinalIgnoreCase))
        {
            var conversation = context.Execution.ConversationId ?? context.WorkflowContext.ConversationId;
            if (!string.IsNullOrWhiteSpace(conversation))
                correlationId = conversation;
        }

        var timeoutSeconds =
            ActivityConfigurationReader.GetOptionalInt(configuration, "timeoutSeconds")
            ?? ActivityConfigurationReader.GetOptionalInt(configuration, "timeout");

        DateTimeOffset? resumeAt = null;
        if (timeoutSeconds is int seconds)
        {
            if (seconds <= 0)
            {
                throw new ActivityConfigurationException(
                    WaitForEventActivity.TypeName, "timeoutSeconds must be positive.");
            }

            resumeAt = context.UtcNow.AddSeconds(seconds);
        }
        else if (context.Step.Timeout is TimeSpan timeout && timeout > TimeSpan.Zero)
        {
            resumeAt = context.UtcNow.Add(timeout);
        }

        return Task.FromResult(
            ActivityExecutionResult.WaitForEvent(eventType, correlationId, resumeAt));
    }
}
