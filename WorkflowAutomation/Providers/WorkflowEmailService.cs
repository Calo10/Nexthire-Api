using Nexa.WorkflowAutomation.Contracts.Providers;
using NextHireEmail = nexthire_api.Services.IEmailService;

namespace nexthire_api.WorkflowAutomation.Providers;

/// <summary>
/// Adapts NextHire <see cref="NextHireEmail"/> to the workflow engine email contract.
/// </summary>
public sealed class WorkflowEmailService : IEmailService
{
    private readonly NextHireEmail _emailService;

    public WorkflowEmailService(NextHireEmail emailService)
    {
        _emailService = emailService;
    }

    public async Task<EmailSendResult> SendAsync(EmailSendRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.To);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Body);

        try
        {
            await _emailService.SendEmailAsync(
                toEmail: request.To,
                subject: request.Subject,
                plainText: request.Body,
                html: $"<div style=\"font-family:sans-serif;white-space:pre-wrap\">{System.Net.WebUtility.HtmlEncode(request.Body).Replace("\n", "<br/>")}</div>",
                cancellationToken: cancellationToken);

            return new EmailSendResult
            {
                Success = true,
                ExternalReferenceId = Guid.NewGuid().ToString("N"),
            };
        }
        catch (Exception ex)
        {
            return new EmailSendResult
            {
                Success = false,
                ErrorCode = "email.send.failed",
                ErrorMessage = ex.Message,
            };
        }
    }
}
