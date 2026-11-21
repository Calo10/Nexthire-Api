using Nexa.WorkflowAutomation.Activities.Communication;
using Nexa.WorkflowAutomation.Contracts.Activities;
using Nexa.WorkflowAutomation.Contracts.Providers;
using Nexa.WorkflowAutomation.Exceptions;
using Nexa.WorkflowAutomation.Serialization;
using Nexa.WorkflowAutomation.Utilities;

namespace nexthire_api.WorkflowAutomation.Activities;

/// <summary>
/// NextHire <c>communication.sendWhatsApp</c>: Messenger WhatsApp + optional Nexa.Documents media URL.
/// </summary>
public sealed class NextHireSendWhatsAppActivity : IWorkflowActivity
{
    private readonly ITemplateRenderer _templateRenderer;
    private readonly IWorkflowVariableResolver _variableResolver;

    public NextHireSendWhatsAppActivity(
        ITemplateRenderer templateRenderer,
        IWorkflowVariableResolver variableResolver)
    {
        _templateRenderer = templateRenderer;
        _variableResolver = variableResolver;
    }

    public string ActivityType => SendWhatsAppActivity.TypeName;

    public async Task<ActivityExecutionResult> ExecuteAsync(
        ActivityExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var configuration = context.Step.Configuration;

        var toVariable =
            ActivityConfigurationReader.GetOptionalString(configuration, "toVariable")
            ?? ActivityConfigurationReader.GetRequiredString(
                SendWhatsAppActivity.TypeName, configuration, "phoneVariable");

        var messageTemplate = ActivityConfigurationReader.GetOptionalString(configuration, "messageTemplate");
        var template = ActivityConfigurationReader.GetOptionalString(configuration, "template");
        var mediaDocumentIdVariable = ActivityConfigurationReader.GetOptionalString(
            configuration, "mediaDocumentIdVariable");
        var saveToConversation = ActivityConfigurationReader.GetOptionalBool(configuration, "saveToConversation");
        var conversationIdVariable = ActivityConfigurationReader.GetOptionalString(
            configuration, "conversationIdVariable");
        var completionEventType = WorkflowReplyWait.ResolveCompletionEventType(
            configuration, WorkflowReplyWait.WhatsAppReplyEvent);

        if (string.IsNullOrWhiteSpace(messageTemplate) && string.IsNullOrWhiteSpace(template))
        {
            throw new ActivityConfigurationException(
                SendWhatsAppActivity.TypeName,
                "'messageTemplate' or 'template' is required.");
        }

        var phone = _variableResolver.ResolveAsString(context.WorkflowContext.Variables, toVariable)
            ?? throw new ActivityConfigurationException(
                SendWhatsAppActivity.TypeName, $"Variable '{toVariable}' is empty.");

        var message = string.IsNullOrWhiteSpace(messageTemplate)
            ? string.Empty
            : _templateRenderer.Render(messageTemplate, context.WorkflowContext.Variables);

        var conversationId = ResolveConversationId(context, conversationIdVariable, phone);

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["correlationId"] = conversationId
        };
        if (!string.IsNullOrWhiteSpace(template))
            metadata["template"] = template;

        var whatsAppService = ActivityServiceLocator.GetRequiredService<IWhatsAppService>(
            context, SendWhatsAppActivity.TypeName);

        var result = await whatsAppService.SendAsync(new WhatsAppSendRequest
        {
            OrganizationId = context.Execution.OrganizationId,
            PhoneNumber = phone,
            Message = message,
            Metadata = metadata
        }, cancellationToken);

        if (!result.Success)
        {
            return ActivityExecutionResult.Failed(
                result.ErrorCode ?? "whatsapp.send.failed",
                result.ErrorMessage ?? "WhatsApp send failed.");
        }

        if (saveToConversation)
        {
            context.Execution.ConversationId ??= conversationId;
            context.WorkflowContext.ConversationId ??= conversationId;

            var conversationService = ActivityServiceLocator.GetOptionalService<IConversationService>(context);
            if (conversationService is not null)
            {
                await conversationService.RegisterOutboundMessageAsync(new RegisterOutboundMessageRequest
                {
                    OrganizationId = context.Execution.OrganizationId,
                    ConversationId = conversationId,
                    Channel = "whatsapp",
                    Body = message,
                    ExternalMessageId = result.ExternalReferenceId
                }, cancellationToken);
            }
        }

        var messageId = result.ExternalReferenceId
            ?? throw new ProviderOperationException("whatsapp", "Messenger did not return messageId.");

        var output = WorkflowJsonSerializer.SerializeToElement(new
        {
            messageId,
            conversationId
        });

        if (string.IsNullOrWhiteSpace(completionEventType))
            return ActivityExecutionResult.Completed(output);

        // Correlate inbound replies by conversation (phone), not a local GUID.
        return ActivityExecutionResult.WaitForEvent(
            completionEventType.Trim(),
            conversationId,
            resumeAt: null,
            output);
    }

    private string ResolveConversationId(
        ActivityExecutionContext context,
        string? conversationIdVariable,
        string phone)
    {
        if (!string.IsNullOrWhiteSpace(conversationIdVariable))
        {
            var fromVariable = _variableResolver.ResolveAsString(
                context.WorkflowContext.Variables, conversationIdVariable);
            if (!string.IsNullOrWhiteSpace(fromVariable))
                return NormalizePhone(fromVariable);
        }

        var existing = context.Execution.ConversationId ?? context.WorkflowContext.ConversationId;
        if (!string.IsNullOrWhiteSpace(existing))
            return NormalizePhone(existing);

        return NormalizePhone(phone);
    }

    private static string NormalizePhone(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["whatsapp:".Length..].Trim();
        return trimmed.StartsWith('+') ? trimmed : $"+{trimmed.TrimStart('+')}";
    }
}
