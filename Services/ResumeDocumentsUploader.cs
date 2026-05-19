using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using nexthire_api.Models.Public;

namespace nexthire_api.Services;

public class ResumeDocumentsUploader : IResumeDocumentsUploader
{
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

        const long maxBytes = 10 * 1024 * 1024; // 10 MB
        if (resume.Length > maxBytes)
            throw new InvalidOperationException("resume file is too large (max 10MB)");

        var ext = Path.GetExtension(resume.FileName ?? string.Empty).ToLowerInvariant();
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf", ".doc", ".docx" };
        if (string.IsNullOrWhiteSpace(ext) || !allowed.Contains(ext))
            throw new InvalidOperationException("resume file type not allowed (pdf, doc, docx)");

        var baseUrl =
            _configuration["DOCUMENTS_FUNCTION_BASE_URL"]
            ?? _configuration["Documents:BaseUrl"];

        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("Documents function base URL is not configured. Set env var DOCUMENTS_FUNCTION_BASE_URL.");

        var trimmed = baseUrl.TrimEnd('/');
        var withApi = trimmed.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? trimmed : $"{trimmed}/api";

        using var client = _httpClientFactory.CreateClient("Documents");
        var fileName = string.IsNullOrWhiteSpace(resume.FileName) ? "resume" + ext : resume.FileName;

        var urlsToTry = new[]
        {
            $"{withApi}/NextHire/{orgId}documents",
            $"{withApi}/NextHire/{orgId}/documents"
        };

        string? lastBody = null;
        int? lastStatus = null;

        foreach (var url in urlsToTry)
        {
            using var form = new MultipartFormDataContent();
            await using var stream = resume.OpenReadStream();
            using var fileContent = new StreamContent(stream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(resume.ContentType) ? "application/octet-stream" : resume.ContentType);
            form.Add(fileContent, "file", fileName);

            using var resp = await client.PostAsync(url, form, cancellationToken);
            lastStatus = (int)resp.StatusCode;
            lastBody = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (resp.IsSuccessStatusCode)
            {
                var documentId = ExtractDocumentIdOrNull(lastBody);
                if (string.IsNullOrWhiteSpace(documentId))
                    throw new InvalidOperationException("Documents function response missing documentId");

                return documentId;
            }

            if ((int)resp.StatusCode == 404)
                continue;

            _logger.LogWarning("Documents function upload failed. Url: {Url}. Status: {StatusCode}. Body: {Body}", url, (int)resp.StatusCode, lastBody);
            throw new InvalidOperationException($"Failed to upload resume document (status {(int)resp.StatusCode}).");
        }

        _logger.LogWarning(
            "Documents function endpoint not found (404). Tried: {Url1} and {Url2}. LastStatus: {LastStatus}. LastBody: {LastBody}",
            urlsToTry[0], urlsToTry[1], lastStatus, lastBody);

        throw new InvalidOperationException("Failed to upload resume document (documents endpoint not found).");
    }

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
