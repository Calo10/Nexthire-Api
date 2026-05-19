namespace nexthire_api.Services;

public interface IEmailService
{
    Task SendEmailAsync(string toEmail, string subject, string plainText, string html, CancellationToken cancellationToken = default);
    Task SendMagicLinkAsync(string toEmail, string magicLinkToken, CancellationToken cancellationToken = default);
    Task SendPasswordResetLinkAsync(string toEmail, string resetToken, CancellationToken cancellationToken = default);
}
