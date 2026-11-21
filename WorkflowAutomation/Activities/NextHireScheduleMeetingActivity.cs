using System.Text.Json;
using Nexa.WorkflowAutomation.Activities.Meetings;
using Nexa.WorkflowAutomation.Contracts.Activities;
using Nexa.WorkflowAutomation.Contracts.Providers;
using Nexa.WorkflowAutomation.Enums;
using Nexa.WorkflowAutomation.Exceptions;
using Nexa.WorkflowAutomation.Serialization;
using Nexa.WorkflowAutomation.Utilities;

namespace nexthire_api.WorkflowAutomation.Activities;

/// <summary>
/// V1: resolve Calendly scheduling URL and email it to the invitee.
/// </summary>
public sealed class NextHireScheduleMeetingActivity : IWorkflowActivity
{
    private readonly ITemplateRenderer _templateRenderer;
    private readonly IWorkflowVariableResolver _variableResolver;

    public NextHireScheduleMeetingActivity(
        ITemplateRenderer templateRenderer,
        IWorkflowVariableResolver variableResolver)
    {
        _templateRenderer = templateRenderer;
        _variableResolver = variableResolver;
    }

    public string ActivityType => ScheduleMeetingActivity.TypeName;

    public async Task<ActivityExecutionResult> ExecuteAsync(
        ActivityExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var configuration = context.Step.Configuration;

        var inviteeEmailVariable =
            ActivityConfigurationReader.GetOptionalString(configuration, "inviteeEmailVariable")
            ?? ActivityConfigurationReader.GetOptionalString(configuration, "participantEmailVariable")
            ?? "Candidate.Email";

        var inviteeEmail = _variableResolver.ResolveAsString(context.WorkflowContext.Variables, inviteeEmailVariable)
            ?? throw new ActivityConfigurationException(
                ScheduleMeetingActivity.TypeName, $"Variable '{inviteeEmailVariable}' is empty.");

        var calendar = ActivityServiceLocator.GetRequiredService<ICalendarService>(
            context, ScheduleMeetingActivity.TypeName);

        var schedule = await calendar.ScheduleAsync(new MeetingScheduleRequest
        {
            OrganizationId = context.Execution.OrganizationId,
            ParticipantEmail = inviteeEmail,
            Title = ActivityConfigurationReader.GetOptionalString(configuration, "titleTemplate") ?? "Interview",
            MeetingType = ActivityConfigurationReader.GetOptionalString(configuration, "eventType"),
        }, cancellationToken);

        if (!schedule.Success || string.IsNullOrWhiteSpace(schedule.SchedulingUrl))
        {
            return ActivityExecutionResult.Failed(
                schedule.ErrorCode ?? "calendly.failed",
                schedule.ErrorMessage ?? "Could not resolve a Calendly scheduling URL.");
        }

        var bookingUrl = schedule.SchedulingUrl;
        context.WorkflowContext.Variables.Set("Meeting.BookingUrl", bookingUrl, WorkflowVariableScope.Execution);
        context.WorkflowContext.Variables.Set("Meeting.Url", bookingUrl, WorkflowVariableScope.Execution);

        var email = ActivityServiceLocator.GetRequiredService<IEmailService>(
            context, ScheduleMeetingActivity.TypeName);
        var subjectTemplate =
            ActivityConfigurationReader.GetOptionalString(configuration, "subjectTemplate")
            ?? "Schedule your interview";
        var bodyTemplate =
            ActivityConfigurationReader.GetOptionalString(configuration, "messageTemplate")
            ?? "Please book a time here: {{Meeting.Url}}";

        var subject = _templateRenderer.Render(subjectTemplate, context.WorkflowContext.Variables);
        var body = _templateRenderer.Render(bodyTemplate, context.WorkflowContext.Variables);

        var send = await email.SendAsync(new EmailSendRequest
        {
            OrganizationId = context.Execution.OrganizationId,
            To = inviteeEmail.Trim(),
            Subject = subject,
            Body = body,
        }, cancellationToken);

        if (!send.Success)
        {
            return ActivityExecutionResult.Failed(
                send.ErrorCode ?? "email.send.failed",
                send.ErrorMessage ?? "Failed to email the scheduling link.");
        }

        var output = WorkflowJsonSerializer.SerializeToElement(new { meetingUrl = bookingUrl });
        return ActivityExecutionResult.Completed(output, new Dictionary<string, JsonElement>
        {
            ["Meeting.Url"] = WorkflowJsonSerializer.SerializeToElement(bookingUrl),
            ["Meeting.BookingUrl"] = WorkflowJsonSerializer.SerializeToElement(bookingUrl),
        });
    }
}
