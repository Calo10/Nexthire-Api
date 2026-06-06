using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace nexthire_api.Services;

public partial class WhatsAppTwilioMediaService : IWhatsAppTwilioMediaService
{
    private const long MaxBytes = 10 * 1024 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WhatsAppTwilioMediaService> _logger;

    public WhatsAppTwilioMediaService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<WhatsAppTwilioMediaService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<WhatsAppTwilioMediaDownload> DownloadAsync(
        string mediaUrl,
        string? fileNameHint = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mediaUrl))
            throw new ArgumentException("mediaUrl is required", nameof(mediaUrl));

        var url = mediaUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("media URL is not valid.");

        var credentials = ResolveTwilioCredentials(uri);
        if (credentials is null)
        {
            throw new InvalidOperationException(
                "Twilio credentials are not configured. Set Twilio:AccountSid and Twilio:AuthToken " +
                "(or TWILIO_ACCOUNT_SID and TWILIO_AUTH_TOKEN).");
        }

        using var client = _httpClientFactory.CreateClient("Twilio");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{credentials.Value.AccountSid}:{credentials.Value.AuthToken}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        _logger.LogInformation(
            "[WhatsAppResume] Twilio download start AccountSid={AccountSid} UrlHost={Host}",
            credentials.Value.AccountSid,
            uri.Host);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "Twilio media download failed. Status={StatusCode} Body={Body}",
                (int)response.StatusCode,
                body.Length > 500 ? body[..500] : body);
            throw new InvalidOperationException($"Failed to download WhatsApp media (status {(int)response.StatusCode}).");
        }

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > MaxBytes)
            throw new InvalidOperationException("resume file is too large (max 10MB)");

        var contentType = response.Content.Headers.ContentType?.MediaType?.Trim()
            ?? "application/octet-stream";

        await using var networkStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new MemoryStream();
        await networkStream.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length > MaxBytes)
            throw new InvalidOperationException("resume file is too large (max 10MB)");

        buffer.Position = 0;
        var fileName = ResolveFileName(
            response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName,
            fileNameHint,
            contentType,
            uri);

        return new WhatsAppTwilioMediaDownload
        {
            Stream = buffer,
            FileName = fileName,
            ContentType = contentType,
            Length = buffer.Length
        };
    }

    private (string AccountSid, string AuthToken)? ResolveTwilioCredentials(Uri mediaUri)
    {
        var accountSid = _configuration["TWILIO_ACCOUNT_SID"]
            ?? _configuration["Twilio:AccountSid"]
            ?? _configuration["MessengerFunction:TwilioAccountSid"]
            ?? _configuration["WhatsApp:TwilioAccountSid"];
        var authToken = _configuration["TWILIO_AUTH_TOKEN"]
            ?? _configuration["Twilio:AuthToken"]
            ?? _configuration["MessengerFunction:TwilioAuthToken"]
            ?? _configuration["WhatsApp:TwilioAuthToken"];

        var fromUrl = TryExtractAccountSidFromTwilioUrl(mediaUri);
        if (!string.IsNullOrWhiteSpace(fromUrl))
            accountSid = string.IsNullOrWhiteSpace(accountSid) ? fromUrl : accountSid;

        if (string.IsNullOrWhiteSpace(accountSid) || string.IsNullOrWhiteSpace(authToken))
            return null;

        return (accountSid.Trim(), authToken.Trim());
    }

    internal static string? TryExtractAccountSidFromTwilioUrl(Uri uri)
    {
        var match = TwilioAccountPathRegex().Match(uri.AbsolutePath);
        return match.Success ? match.Groups["sid"].Value : null;
    }

    internal static string ResolveFileName(
        string? contentDispositionFileName,
        string? fileNameHint,
        string contentType,
        Uri uri)
    {
        var fromHeader = NormalizeFileName(contentDispositionFileName);
        if (!string.IsNullOrWhiteSpace(fromHeader))
            return fromHeader;

        var fromHint = NormalizeFileName(fileNameHint);
        if (!string.IsNullOrWhiteSpace(fromHint))
            return fromHint;

        var ext = ExtensionFromContentType(contentType);
        if (string.IsNullOrWhiteSpace(ext))
            ext = ".pdf";

        return "resume" + ext;
    }

    private static string? NormalizeFileName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;

        trimmed = Uri.UnescapeDataString(trimmed.Replace('+', ' '));
        return Path.GetFileName(trimmed);
    }

    internal static string? ExtensionFromContentType(string contentType) =>
        contentType.Trim().ToLowerInvariant() switch
        {
            "application/pdf" => ".pdf",
            "application/msword" => ".doc",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
            _ => null
        };

    [GeneratedRegex(@"/Accounts/(?<sid>AC[a-zA-Z0-9]+)/", RegexOptions.CultureInvariant)]
    private static partial Regex TwilioAccountPathRegex();
}
