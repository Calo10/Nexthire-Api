using nexthire_api.Options;
using nexthire_api.Services;

namespace nexthire_api.WorkflowAutomation.Providers;

public sealed class WhatsAppSendMessagingRequest
{
    public Guid OrganizationId { get; set; }
    public string To { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? CorrelationId { get; set; }
    public string? TemplateName { get; set; }
    public string? MediaUrl { get; set; }
}

public sealed class WhatsAppSendMessagingResult
{
    public bool Success { get; set; }
    public string? MessageId { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public interface IWhatsAppMessagingService
{
    Task<WhatsAppSendMessagingResult> SendAsync(
        WhatsAppSendMessagingRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class WhatsAppMessagingService : IWhatsAppMessagingService
{
    private readonly INexaMessengerWhatsAppClient _messenger;
    private readonly ITwilioOrgCredentialsResolver _twilio;
    private readonly ILogger<WhatsAppMessagingService> _logger;

    public WhatsAppMessagingService(
        INexaMessengerWhatsAppClient messenger,
        ITwilioOrgCredentialsResolver twilio,
        ILogger<WhatsAppMessagingService> logger)
    {
        _messenger = messenger;
        _twilio = twilio;
        _logger = logger;
    }

    public async Task<WhatsAppSendMessagingResult> SendAsync(
        WhatsAppSendMessagingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var credentials = await _twilio.ResolveAsync(request.OrganizationId, cancellationToken);
            var messageId = await _messenger.SendMessageAsync(
                request.OrganizationId.ToString("D"),
                request.To,
                request.Body,
                credentials,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(messageId))
            {
                return new WhatsAppSendMessagingResult
                {
                    Success = false,
                    ErrorCode = "whatsapp.send.failed",
                    ErrorMessage = "Messenger returned an empty message id.",
                };
            }

            return new WhatsAppSendMessagingResult
            {
                Success = true,
                MessageId = messageId,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WhatsApp messaging failed for org {OrgId}", request.OrganizationId);
            return new WhatsAppSendMessagingResult
            {
                Success = false,
                ErrorCode = "whatsapp.send.failed",
                ErrorMessage = ex.Message,
            };
        }
    }
}
