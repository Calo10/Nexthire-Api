using Nexa.WorkflowAutomation.Contracts.Providers;

namespace nexthire_api.WorkflowAutomation.Providers;

/// <summary>
/// Adapts <see cref="IWhatsAppMessagingService"/> to the workflow engine WhatsApp contract.
/// </summary>
public sealed class WorkflowWhatsAppService : IWhatsAppService
{
    private readonly IWhatsAppMessagingService _messaging;

    public WorkflowWhatsAppService(IWhatsAppMessagingService messaging)
    {
        _messaging = messaging;
    }

    public async Task<MessageSendResult> SendAsync(
        WhatsAppSendRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PhoneNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Message);

        string? mediaUrl = null;
        string? correlationId = null;
        if (request.Metadata is not null)
        {
            request.Metadata.TryGetValue("mediaUrl", out mediaUrl);
            request.Metadata.TryGetValue("correlationId", out correlationId);
        }

        var result = await _messaging.SendAsync(new WhatsAppSendMessagingRequest
        {
            OrganizationId = request.OrganizationId,
            To = request.PhoneNumber,
            Body = request.Message,
            CorrelationId = correlationId,
            MediaUrl = mediaUrl,
        }, cancellationToken);

        return new MessageSendResult
        {
            Success = result.Success,
            ExternalReferenceId = result.MessageId,
            ErrorCode = result.ErrorCode,
            ErrorMessage = result.ErrorMessage,
        };
    }
}
