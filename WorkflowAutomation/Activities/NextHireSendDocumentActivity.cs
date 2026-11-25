using Nexa.WorkflowAutomation.Contracts.Activities;
using Nexa.WorkflowAutomation.Contracts.Providers;
using Nexa.WorkflowAutomation.Exceptions;
using Nexa.WorkflowAutomation.Serialization;
using Nexa.WorkflowAutomation.Utilities;
using Nexa.WorkflowAutomation.Activities.Documents;

namespace nexthire_api.WorkflowAutomation.Activities;

/// <summary>
/// NextHire <c>documents.send</c>: Nexa.Documents SAS URL → Messenger email.
/// </summary>
public sealed class NextHireSendDocumentActivity : IWorkflowActivity
{
    private readonly ITemplateRenderer _templateRenderer;
    private readonly IWorkflowVariableResolver _variableResolver;

    public NextHireSendDocumentActivity(
        ITemplateRenderer templateRenderer,
        IWorkflowVariableResolver variableResolver)
    {
        _templateRenderer = templateRenderer;
        _variableResolver = variableResolver;
    }

    public string ActivityType => SendDocumentActivity.TypeName;

    public async Task<ActivityExecutionResult> ExecuteAsync(
        ActivityExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var configuration = context.Step.Configuration;

        var documentIdVariable = ActivityConfigurationReader.GetRequiredString(
            SendDocumentActivity.TypeName, configuration, "documentIdVariable");
        var recipientVariable = ActivityConfigurationReader.GetRequiredString(
            SendDocumentActivity.TypeName, configuration, "recipientVariable");
        var subjectTemplate = ActivityConfigurationReader.GetOptionalString(configuration, "subjectTemplate")
            ?? "Document shared with you";
        var messageTemplate = ActivityConfigurationReader.GetOptionalString(configuration, "messageTemplate");

        var documentId = _variableResolver.ResolveAsString(context.WorkflowContext.Variables, documentIdVariable)
            ?? throw new ActivityConfigurationException(
                SendDocumentActivity.TypeName, $"Variable '{documentIdVariable}' is empty.");
        var recipient = _variableResolver.ResolveAsString(context.WorkflowContext.Variables, recipientVariable)
            ?? throw new ActivityConfigurationException(
                SendDocumentActivity.TypeName, $"Variable '{recipientVariable}' is empty.");

        var subject = _templateRenderer.Render(subjectTemplate, context.WorkflowContext.Variables);
        var message = string.IsNullOrWhiteSpace(messageTemplate)
            ? null
            : _templateRenderer.Render(messageTemplate, context.WorkflowContext.Variables);

        var documentService = ActivityServiceLocator.GetRequiredService<IDocumentService>(
            context, SendDocumentActivity.TypeName);

        var result = await documentService.SendAsync(new DocumentSendRequest
        {
            OrganizationId = context.Execution.OrganizationId,
            DocumentId = documentId,
            Recipient = recipient,
            Message = message,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["subject"] = subject
            }
        }, cancellationToken);

        if (!result.Success)
        {
            return ActivityExecutionResult.Failed(
                result.ErrorCode ?? "document.send.failed",
                result.ErrorMessage ?? "Document send failed.");
        }

        var output = WorkflowJsonSerializer.SerializeToElement(new
        {
            documentId,
            documentUrl = result.DocumentUrl,
            messageId = result.ExternalReferenceId
        });

        return ActivityExecutionResult.Completed(output);
    }
}
