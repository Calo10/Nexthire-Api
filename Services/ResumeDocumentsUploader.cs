using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using nexthire_api.Models.Public;

namespace nexthire_api.Services;

public class ResumeDocumentsUploader : IResumeDocumentsUploader
{
    private const string FunctionKeyHeaderName = "x-functions-key";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ResumeDocumentsUploader> _logger;

    public ResumeDocumentsUploader(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<ResumeDocumentsUploader> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> UploadResumeAsync(Guid orgId, IFormFile resume, CancellationToken cancellationToken = default)
    {
        if (resume is null || resume.Length <= 0)
            throw new ArgumentException("resume file is required", nameof(resume));

        await using var stream = resume.OpenReadStream();
        return await UploadResumeAsync(
            orgId,
            stream,
            string.IsNullOrWhiteSpace(resume.FileName) ? "resume" : resume.FileName,
            resume.ContentType,
            resume.Length,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string> UploadResumeAsync(
        Guid orgId,
        Stream content,
        string fileName,
        string? contentType,
        long length,
        CancellationToken cancellationToken = default)
    {
        if (content is null || !content.CanRead)
            throw new ArgumentException("resume stream is required", nameof(content));

        if (length <= 0)
            throw new ArgumentException("resume file is required", nameof(length));

        const long maxBytes = 10 * 1024 * 1024; // 10 MB
        if (length > maxBytes)
            throw new InvalidOperationException("resume file is too large (max 10MB)");

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(ext))
            ext = ExtensionFromContentType(contentType ?? string.Empty) ?? string.Empty;

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf", ".doc", ".docx" };
        if (string.IsNullOrWhiteSpace(ext) || !allowed.Contains(ext))
            throw new InvalidOperationException("resume file type not allowed (pdf, doc, docx)");

        var baseUrl = ResolveDocumentsBaseUrl(_configuration);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException(
                "Documents function base URL is not configured. Set DOCUMENTS_FUNCTION_BASE_URL or Documents:BaseUrl " +
                "(Azure App Service: same host as DocumentService__BaseUrl — the documents Function App, not nexthire-api).");
        }

        var functionKey = ResolveDocumentsFunctionKey(_configuration);
        var uploadUri = BuildDocumentsUploadUri(baseUrl, orgId, functionKey);
        var resolvedFileName = string.IsNullOrWhiteSpace(Path.GetFileName(fileName))
            ? "resume" + ext
            : Path.GetFileName(fileName);

        _logger.LogInformation(
            "[WhatsAppResume] Documents POST OrgId={OrgId} HasFunctionKey={HasFunctionKey} Url={Url} FileName={FileName} Bytes={Bytes}",
            orgId,
            !string.IsNullOrWhiteSpace(functionKey),
            RedactSecrets(uploadUri.ToString()),
            resolvedFileName,
            length);

        using var client = _httpClientFactory.CreateClient("Documents");

        using var form = new MultipartFormDataContent();
        using var fileContent = new StreamContent(content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
        form.Add(fileContent, "file", resolvedFileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, uploadUri) { Content = form };
        if (!string.IsNullOrWhiteSpace(functionKey))
            request.Headers.TryAddWithoutValidation(FunctionKeyHeaderName, functionKey);

        using var resp = await client.SendAsync(request, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (resp.IsSuccessStatusCode)
        {
            var documentId = ExtractDocumentIdOrNull(body);
            if (string.IsNullOrWhiteSpace(documentId))
                throw new InvalidOperationException("Documents function response missing documentId");

            _logger.LogInformation(
                "[WhatsAppResume] Documents upload OK OrgId={OrgId} DocumentId={DocumentId}",
                orgId,
                documentId);

            return documentId;
        }

        _logger.LogWarning(
            "[WhatsAppResume] Documents upload FAILED Url: {Url} (base {BaseUrl}). HasFunctionKey={HasFunctionKey}. Status: {StatusCode}. Body: {Body}",
            RedactSecrets(uploadUri.ToString()),
            RedactSecrets(baseUrl),
            !string.IsNullOrWhiteSpace(functionKey),
            (int)resp.StatusCode,
            body);

        if ((int)resp.StatusCode == 401)
        {
            throw new InvalidOperationException(
                "Failed to upload resume document (status 401). " +
                "Documents function rejected the request. Confirm Documents__FunctionKey (or DOCUMENTS_FUNCTION_KEY) " +
                "matches the 'code' from Get function URL on PostDocuments, and restart nexthire-api after changing app settings.");
        }

        if ((int)resp.StatusCode == 404)
        {
            var portHint = baseUrl.Contains(":7071", StringComparison.OrdinalIgnoreCase)
                ? " Port 7071 is often nexa-messenger locally; run nexa-documents on http://localhost:7072 (func start --port 7072) or use https://nexa-documents-function.azurewebsites.net."
                : string.Empty;

            throw new InvalidOperationException(
                "Failed to upload resume document (documents endpoint not found). " +
                $"Verify DOCUMENTS_FUNCTION_BASE_URL points to the documents Azure Function App " +
                $"(expected POST /api/NextHire/{{orgId}}/documents). Configured base: {RedactSecrets(baseUrl)}.{portHint}");
        }

        throw new InvalidOperationException($"Failed to upload resume document (status {(int)resp.StatusCode}).");
    }

    internal static string? ResolveDocumentsBaseUrl(IConfiguration configuration) =>
        configuration["DOCUMENTS_FUNCTION_BASE_URL"]
        ?? configuration["Documents:BaseUrl"]
        ?? configuration["DocumentService:BaseUrl"];

    internal static string? ResolveDocumentsFunctionKey(IConfiguration configuration)
    {
        var raw =
            configuration["DOCUMENTS_FUNCTION_KEY"]
            ?? configuration["Documents:FunctionKey"]
            ?? configuration["Documents:Code"];

        return NormalizeFunctionKey(raw);
    }

    /// <summary>Accepts raw key, full function URL, or <c>code=...</c> pasted from Azure portal.</summary>
    internal static string? NormalizeFunctionKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var key = raw.Trim().Trim('"');

        if (Uri.TryCreate(key, UriKind.Absolute, out var uri))
        {
            var query = uri.Query.TrimStart('?');
            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.StartsWith("code=", StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(part["code=".Length..]);
            }
        }

        if (key.StartsWith("code=", StringComparison.OrdinalIgnoreCase))
            key = key["code=".Length..];

        key = key.Trim();
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    internal static Uri BuildDocumentsUploadUri(string baseUrl, Guid orgId, string? functionKey) =>
        BuildDocumentsUri(baseUrl, $"{orgId}/documents", functionKey);

    internal static Uri BuildDocumentsUri(string baseUrl, string pathAfterNextHire, string? functionKey)
    {
        var trimmed = baseUrl.TrimEnd('/');
        var withApi = trimmed.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? trimmed : $"{trimmed}/api";
        var builder = new UriBuilder($"{withApi}/NextHire/{pathAfterNextHire.TrimStart('/')}");

        if (!string.IsNullOrWhiteSpace(functionKey))
            builder.Query = $"code={Uri.EscapeDataString(functionKey)}";

        return builder.Uri;
    }

    internal static void ApplyDocumentsFunctionAuth(HttpRequestMessage request, string? functionKey)
    {
        if (string.IsNullOrWhiteSpace(functionKey))
            return;

        request.Headers.TryAddWithoutValidation(FunctionKeyHeaderName, functionKey);
    }

    private static string RedactSecrets(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var redacted = value;
        const string marker = "code=";
        var idx = redacted.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
            redacted = redacted[..(idx + marker.Length)] + "***";

        return redacted;
    }

    private static string? ExtensionFromContentType(string contentType) =>
        contentType.Trim().ToLowerInvariant() switch
        {
            "application/pdf" => ".pdf",
            "application/msword" => ".doc",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
            _ => null
        };

    private string? ExtractDocumentIdOrNull(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            var parsed = JsonSerializer.Deserialize<DocumentUploadResponse>(raw, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return string.IsNullOrWhiteSpace(parsed?.DocumentId) ? null : parsed.DocumentId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse documents function response: {Body}", raw);
            return null;
        }
    }
}
