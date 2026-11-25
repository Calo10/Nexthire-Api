using nexthire_api.WorkflowAutomation.Documents;
using Nexa.WorkflowAutomation.Activities.Documents;
using Nexa.WorkflowAutomation.Contracts.Activities;
using Nexa.WorkflowAutomation.Contracts.Providers;
using Nexa.WorkflowAutomation.Exceptions;
using Nexa.WorkflowAutomation.Serialization;
using Nexa.WorkflowAutomation.Utilities;

namespace nexthire_api.WorkflowAutomation.Activities;

/// <summary>
/// NextHire <c>documents.sendForSignature</c>:
/// Nexa.Documents SAS URL → signature provider → WaitingForEvent.
/// </summary>
public sealed class NextHireSendForSignatureActivity : IWorkflowActivity
{
    private readonly ITemplateRenderer _templateRenderer;
    private readonly IWorkflowVariableResolver _variableResolver;

    public NextHireSendForSignatureActivity(
        ITemplateRenderer templateRenderer,
        IWorkflowVariableResolver variableResolver)
    {
        _templateRenderer = templateRenderer;
        _variableResolver = variableResolver;
    }

    public string ActivityType => SendForSignatureActivity.TypeName;

    public async Task<ActivityExecutionResult> ExecuteAsync(
        ActivityExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var configuration = context.Step.Configuration;

        var documentIdVariable = ActivityConfigurationReader.GetRequiredString(
            SendForSignatureActivity.TypeName, configuration, "documentIdVariable");
        var recipientNameVariable = ActivityConfigurationReader.GetRequiredString(
            SendForSignatureActivity.TypeName, configuration, "recipientNameVariable");
        var recipientEmailVariable = ActivityConfigurationReader.GetRequiredString(
            SendForSignatureActivity.TypeName, configuration, "recipientEmailVariable");
        var subjectTemplate = ActivityConfigurationReader.GetRequiredString(
            SendForSignatureActivity.TypeName, configuration, "subjectTemplate");
        var messageTemplate = ActivityConfigurationReader.GetOptionalString(configuration, "messageTemplate");
        var completionEventType = ActivityConfigurationReader.GetOptionalString(configuration, "completionEventType")
            ?? "signature.completed";
        var timeoutSeconds = ActivityConfigurationReader.GetOptionalInt(configuration, "timeoutSeconds");

        var documentId = _variableResolver.ResolveAsString(context.WorkflowContext.Variables, documentIdVariable)
            ?? throw new ActivityConfigurationException(
                SendForSignatureActivity.TypeName, $"Variable '{documentIdVariable}' is empty.");
        var recipientName = _variableResolver.ResolveAsString(context.WorkflowContext.Variables, recipientNameVariable)
            ?? throw new ActivityConfigurationException(
                SendForSignatureActivity.TypeName, $"Variable '{recipientNameVariable}' is empty.");
        var recipientEmail = _variableResolver.ResolveAsString(context.WorkflowContext.Variables, recipientEmailVariable)
            ?? throw new ActivityConfigurationException(
                SendForSignatureActivity.TypeName, $"Variable '{recipientEmailVariable}' is empty.");

        var subject = _templateRenderer.Render(subjectTemplate, context.WorkflowContext.Variables);
        var message = string.IsNullOrWhiteSpace(messageTemplate)
            ? null
            : _templateRenderer.Render(messageTemplate, context.WorkflowContext.Variables);

        var documentsClient = ActivityServiceLocator.GetRequiredService<INexaDocumentsClient>(
            context, SendForSignatureActivity.TypeName);
        var download = await documentsClient.CreateDownloadUrlAsync(
            context.Execution.OrganizationId,
            documentId,
            cancellationToken);

        var signatureService = ActivityServiceLocator.GetRequiredService<ISignatureService>(
            context, SendForSignatureActivity.TypeName);

        var result = await signatureService.SendForSignatureAsync(new SignatureSendRequest
        {
            OrganizationId = context.Execution.OrganizationId,
            DocumentId = documentId,
            RecipientName = recipientName,
            RecipientEmail = recipientEmail,
            Subject = subject,
            Message = message,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["documentUrl"] = download.Url,
                ["workflowExecutionId"] = context.Execution.Id.ToString("D"),
                ["organizationId"] = context.Execution.OrganizationId.ToString("D")
            }
        }, cancellationToken);

        if (!result.Success)
        {
            return ActivityExecutionResult.Failed(
                result.ErrorCode ?? "signature.send.failed",
                result.ErrorMessage ?? "Send for signature failed.");
        }

        var signatureRequestId = result.EnvelopeId ?? result.ExternalReferenceId
            ?? throw new ProviderOperationException("signature", "Signature request identifier was not returned.");

        DateTimeOffset? resumeAt = timeoutSeconds is int seconds
            ? context.UtcNow.AddSeconds(seconds)
            : null;

        var output = WorkflowJsonSerializer.SerializeToElement(new
        {
            documentId,
            documentUrl = download.Url,
            signatureRequestId
        });

        return ActivityExecutionResult.WaitForEvent(completionEventType, signatureRequestId, resumeAt, output);
    }
}
