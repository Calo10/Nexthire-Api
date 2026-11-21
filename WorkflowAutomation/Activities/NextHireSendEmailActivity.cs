using Nexa.WorkflowAutomation.Activities.Communication;
using Nexa.WorkflowAutomation.Contracts.Activities;
using Nexa.WorkflowAutomation.Contracts.Providers;
using Nexa.WorkflowAutomation.Exceptions;
using Nexa.WorkflowAutomation.Serialization;
using Nexa.WorkflowAutomation.Utilities;

namespace nexthire_api.WorkflowAutomation.Activities;

/// <summary>
/// NextHire <c>communication.sendEmail</c>: Messenger send + org-scoped conversation id (recipient email),
/// matching WhatsApp phone correlation. Optional wait via <c>completionEventType</c>.
/// </summary>
public sealed class NextHireSendEmailActivity : IWorkflowActivity
{
    private readonly ITemplateRenderer _templateRenderer;
    private readonly IWorkflowVariableResolver _variableResolver;

    public NextHireSendEmailActivity(
        ITemplateRenderer templateRenderer,
        IWorkflowVariableResolver variableResolver)
    {
        _templateRenderer = templateRenderer;
        _variableResolver = variableResolver;
    }

    public string ActivityType => SendEmailActivity.TypeName;

    public async Task<ActivityExecutionResult> ExecuteAsync(
        ActivityExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var configuration = context.Step.Configuration;
        var toVariable = ActivityConfigurationReader.GetRequiredString(
            SendEmailActivity.TypeName, configuration, "toVariable");
        var subjectTemplate = ActivityConfigurationReader.GetRequiredString(
            SendEmailActivity.TypeName, configuration, "subjectTemplate");
        var bodyTemplate =
            ActivityConfigurationReader.GetOptionalString(configuration, "bodyTemplate")
            ?? ActivityConfigurationReader.GetOptionalString(configuration, "messageTemplate")
            ?? ActivityConfigurationReader.GetOptionalString(configuration, "template")
            ?? throw new ActivityConfigurationException(
                SendEmailActivity.TypeName, "'bodyTemplate' (or 'template') is required.");

        var saveToConversation = ActivityConfigurationReader.GetOptionalBool(configuration, "saveToConversation");
        var conversationIdVariable = ActivityConfigurationReader.GetOptionalString(
            configuration, "conversationIdVariable");
        var completionEventType = WorkflowReplyWait.ResolveCompletionEventType(
            configuration, WorkflowReplyWait.EmailReplyEvent);

        var to = _variableResolver.ResolveAsString(context.WorkflowContext.Variables, toVariable)
            ?? throw new ActivityConfigurationException(
                SendEmailActivity.TypeName, $"Variable '{toVariable}' is empty.");
        var normalizedTo = EmailTenantResolver.NormalizeEmail(to);
        if (string.IsNullOrWhiteSpace(normalizedTo))
        {
            throw new ActivityConfigurationException(
                SendEmailActivity.TypeName, $"Variable '{toVariable}' is not a valid email.");
        }

        var subject = _templateRenderer.Render(subjectTemplate, context.WorkflowContext.Variables);
        var body = _templateRenderer.Render(bodyTemplate, context.WorkflowContext.Variables);

        var emailService = ActivityServiceLocator.GetRequiredService<IEmailService>(
            context, SendEmailActivity.TypeName);
        var result = await emailService.SendAsync(new EmailSendRequest
        {
            OrganizationId = context.Execution.OrganizationId,
            To = normalizedTo,
            Subject = subject,
            Body = body
        }, cancellationToken);

        if (!result.Success)
        {
            return ActivityExecutionResult.Failed(
                result.ErrorCode ?? "email.send.failed",
                result.ErrorMessage ?? "Email send failed.");
        }

        var conversationId = ResolveConversationId(
            context, conversationIdVariable, normalizedTo);

        context.Execution.ConversationId = conversationId;
        context.WorkflowContext.ConversationId = conversationId;

        if (saveToConversation)
        {
            var conversationService = ActivityServiceLocator.GetOptionalService<IConversationService>(context);
            if (conversationService is not null)
            {
                await conversationService.RegisterOutboundMessageAsync(new RegisterOutboundMessageRequest
                {
                    OrganizationId = context.Execution.OrganizationId,
                    ConversationId = conversationId,
                    Channel = "email",
                    Body = body,
                    ExternalMessageId = result.ExternalReferenceId
                }, cancellationToken);
            }
        }

        var output = WorkflowJsonSerializer.SerializeToElement(new
        {
            messageId = result.ExternalReferenceId,
            conversationId
        });

        if (string.IsNullOrWhiteSpace(completionEventType))
            return ActivityExecutionResult.Completed(output);

        return ActivityExecutionResult.WaitForEvent(
            completionEventType.Trim(),
            conversationId,
            resumeAt: null,
            output);
    }

    private string ResolveConversationId(
        ActivityExecutionContext context,
        string? conversationIdVariable,
        string normalizedEmail)
    {
        if (!string.IsNullOrWhiteSpace(conversationIdVariable))
        {
            var fromVariable = _variableResolver.ResolveAsString(
                context.WorkflowContext.Variables, conversationIdVariable);
            var normalized = EmailTenantResolver.NormalizeEmail(fromVariable);
            if (!string.IsNullOrWhiteSpace(normalized))
                return EmailTenantResolver.BuildConversationId(
                    context.Execution.OrganizationId, normalized);
        }

        var existing = context.Execution.ConversationId ?? context.WorkflowContext.ConversationId;
        if (!string.IsNullOrWhiteSpace(existing) && existing.Contains('@'))
            return existing.Contains(':')
                ? existing
                : EmailTenantResolver.BuildConversationId(context.Execution.OrganizationId, existing);

        return EmailTenantResolver.BuildConversationId(
            context.Execution.OrganizationId, normalizedEmail);
    }
}
