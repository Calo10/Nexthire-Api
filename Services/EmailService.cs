using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;

namespace nexthire_api.Services;

public class EmailService : IEmailService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailService> _logger;

    public EmailService(HttpClient httpClient, IConfiguration configuration, ILogger<EmailService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public Task SendEmailAsync(string toEmail, string subject, string plainText, string html, CancellationToken cancellationToken = default)
    {
        return SendEmailAsync("direct-send", toEmail, subject, plainText, html, cancellationToken);
    }

    private Task SendEmailAsync(
        string emailType,
        string toEmail,
        string subject,
        string plainText,
        string html,
        CancellationToken cancellationToken)
    {
        return SendEmailViaMessengerFunctionAsync(
            emailType: emailType,
            toEmail: toEmail,
            subject: subject,
            plainText: plainText,
            html: html,
            cancellationToken: cancellationToken);
    }

    public Task SendMagicLinkAsync(string toEmail, string magicLinkToken, CancellationToken cancellationToken = default)
    {
        var frontendBaseUrl = ResolveFrontendBaseUrl();
        var verifyUrl = $"{frontendBaseUrl.TrimEnd('/')}/auth/verify?token={Uri.EscapeDataString(magicLinkToken)}";
        var subject = "Tu enlace de acceso a NextHire";
        var plainText =
            $"Hola,\n\nUsa este enlace para iniciar sesion en NextHire:\n{verifyUrl}\n\n" +
            "Si no solicitaste este acceso, ignora este mensaje.";
        var html =
            $"<p>Hola,</p><p>Usa este enlace para iniciar sesion en NextHire:</p>" +
            $"<p><a href=\"{WebUtility.HtmlEncode(verifyUrl)}\">Iniciar sesion</a></p>" +
            "<p>Si no solicitaste este acceso, ignora este mensaje.</p>";

        return SendEmailAsync("magic-link", toEmail, subject, plainText, html, cancellationToken);
    }

    public Task SendPasswordResetLinkAsync(string toEmail, string resetToken, CancellationToken cancellationToken = default)
    {
        var frontendBaseUrl = ResolveFrontendBaseUrl();
        var resetUrl = $"{frontendBaseUrl.TrimEnd('/')}/reset-password?token={Uri.EscapeDataString(resetToken)}";
        var subject = "Restablece tu contrasena de NextHire";
        var plainText =
            $"Hola,\n\nRecibimos una solicitud para restablecer tu contrasena.\n" +
            $"Usa este enlace:\n{resetUrl}\n\nSi no fuiste tu, ignora este mensaje.";
        var html =
            $"<p>Hola,</p><p>Recibimos una solicitud para restablecer tu contrasena.</p>" +
            $"<p><a href=\"{WebUtility.HtmlEncode(resetUrl)}\">Restablecer contrasena</a></p>" +
            "<p>Si no fuiste tu, ignora este mensaje.</p>";

        return SendEmailAsync("password-reset", toEmail, subject, plainText, html, cancellationToken);
    }

    private async Task SendEmailViaMessengerFunctionAsync(
        string emailType,
        string toEmail,
        string subject,
        string plainText,
        string html,
        CancellationToken cancellationToken)
    {
        var messengerUrl = _configuration["MessengerFunction:Url"];
        var messengerCode = _configuration["MessengerFunction:Code"];
        var tenantId = _configuration["MessengerFunction:TenantId"];
        var fromName = _configuration["MessengerFunction:FromName"] ?? "NextHire";

        if (string.IsNullOrWhiteSpace(messengerUrl) ||
            string.IsNullOrWhiteSpace(messengerCode) ||
            string.IsNullOrWhiteSpace(tenantId))
        {
            _logger.LogWarning(
                "Messenger Function config missing. Skipping email send. Type: {EmailType}, To: {ToEmail}",
                emailType,
                toEmail);
            return;
        }

        var correlationId = Activity.Current?.Id ?? Guid.NewGuid().ToString("N");
        var requestUrl = $"{messengerUrl}?code={Uri.EscapeDataString(messengerCode)}";

        var payload = new
        {
            tenantId,
            correlationId,
            fromName,
            to = new[] { toEmail },
            subject,
            plainText,
            html
        };

        _logger.LogInformation(
            "Sending email via Messenger Function. Type: {EmailType}, TenantId: {TenantId}, To: {ToEmail}, CorrelationId: {CorrelationId}",
            emailType,
            tenantId,
            toEmail,
            correlationId);

        try
        {
            var response = await _httpClient.PostAsJsonAsync(requestUrl, payload, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var truncatedResponse = Truncate(responseBody, 600);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Messenger Function HTTP failure. Type: {EmailType}, StatusCode: {StatusCode}, CorrelationId: {CorrelationId}, Response: {Response}",
                    emailType,
                    (int)response.StatusCode,
                    correlationId,
                    truncatedResponse);

                throw new InvalidOperationException(
                    $"Messenger Function returned non-success status code {(int)response.StatusCode}.");
            }

            _logger.LogInformation(
                "Email sent via Messenger Function successfully. Type: {EmailType}, StatusCode: {StatusCode}, CorrelationId: {CorrelationId}, Response: {Response}",
                emailType,
                (int)response.StatusCode,
                correlationId,
                truncatedResponse);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected error sending email via Messenger Function. Type: {EmailType}, To: {ToEmail}, CorrelationId: {CorrelationId}",
                emailType,
                toEmail,
                correlationId);
            throw;
        }
    }

    private string ResolveFrontendBaseUrl()
    {
        return _configuration["Frontend:BaseUrl"]
            ?? _configuration["AppSettings:FrontendUrl"]
            ?? "http://localhost:5173";
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;

        return value[..maxLength];
    }
}
