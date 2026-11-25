using Nexa.WorkflowAutomation.Activities.Flow;
using Nexa.WorkflowAutomation.Contracts.Activities;
using Nexa.WorkflowAutomation.Exceptions;
using Nexa.WorkflowAutomation.Utilities;

namespace nexthire_api.WorkflowAutomation.Activities;

/// <summary>
/// NextHire delay activity for <c>flow.delay</c>.
/// Supports <c>duration</c> + <c>unit</c>, and falls back to the package delay config.
/// </summary>
public sealed class NextHireDelayActivity : IWorkflowActivity
{
    private readonly DelayActivity _packageDelay;

    public NextHireDelayActivity(IWorkflowVariableResolver variableResolver)
    {
        ArgumentNullException.ThrowIfNull(variableResolver);
        _packageDelay = new DelayActivity(variableResolver);
    }

    public string ActivityType => DelayActivity.TypeName;

    public Task<ActivityExecutionResult> ExecuteAsync(
        ActivityExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var configuration = context.Step.Configuration;
        var duration = ActivityConfigurationReader.GetOptionalInt(configuration, "duration");
        var unit = ActivityConfigurationReader.GetOptionalString(configuration, "unit");

        if (duration is not null || !string.IsNullOrWhiteSpace(unit))
        {
            if (duration is not int positiveDuration || positiveDuration <= 0)
            {
                throw new ActivityConfigurationException(
                    DelayActivity.TypeName,
                    "'duration' must be a positive integer.");
            }

            if (string.IsNullOrWhiteSpace(unit))
            {
                throw new ActivityConfigurationException(
                    DelayActivity.TypeName,
                    "'unit' is required when 'duration' is set. Supported: seconds, minutes, hours, days.");
            }

            var resumeAt = context.UtcNow.Add(ResolveDelay(positiveDuration, unit));
            return Task.FromResult(ActivityExecutionResult.WaitUntil(resumeAt));
        }

        // Preserve package config: delaySeconds / resumeAtVariable
        return _packageDelay.ExecuteAsync(context, cancellationToken);
    }

    private static TimeSpan ResolveDelay(int duration, string unit)
    {
        return unit.Trim().ToLowerInvariant() switch
        {
            "seconds" or "second" => TimeSpan.FromSeconds(duration),
            "minutes" or "minute" => TimeSpan.FromMinutes(duration),
            "hours" or "hour" => TimeSpan.FromHours(duration),
            "days" or "day" => TimeSpan.FromDays(duration),
            _ => throw new ActivityConfigurationException(
                DelayActivity.TypeName,
                $"Unsupported unit '{unit}'. Supported: seconds, minutes, hours, days.")
        };
    }
}
