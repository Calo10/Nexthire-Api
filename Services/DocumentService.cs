using System.Net;
using System.Text;
using System.Text.Json;
using nexthire_api.DTOs;

namespace nexthire_api.Services;

public class DocumentService : IDocumentService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DocumentService> _logger;

    public DocumentService(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<DocumentService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string> GetResumeDownloadUrlAsync(Guid orgId, Guid documentId, CancellationToken ct)
    {
        var baseUrl = _configuration["DocumentService:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new DocumentServiceException("DocumentService base URL is not configured.", upstreamStatusCode: null, kind: DocumentServiceErrorKind.Configuration);

        var url = $"{baseUrl.TrimEnd('/')}/api/NextHire/{orgId}/documents/{documentId}/download-url";

        using var client = _httpClientFactory.CreateClient("DocumentService");
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };

        HttpResponseMessage resp;
        try
        {
            resp = await client.SendAsync(request, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "DocumentService timeout. OrgId: {OrgId}, DocumentId: {DocumentId}", orgId, documentId);
            throw new DocumentServiceException("Document service timeout.", upstreamStatusCode: null, kind: DocumentServiceErrorKind.Timeout);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "DocumentService request failed. OrgId: {OrgId}, DocumentId: {DocumentId}", orgId, documentId);
            throw new DocumentServiceException("Document service request failed.", upstreamStatusCode: null, kind: DocumentServiceErrorKind.Network);
        }

        if (!resp.IsSuccessStatusCode)
        {
            var status = (int)resp.StatusCode;
            _logger.LogWarning("DocumentService non-success. OrgId: {OrgId}, DocumentId: {DocumentId}, Status: {Status}",
                orgId, documentId, status);

            var kind = resp.StatusCode switch
            {
                HttpStatusCode.NotFound => DocumentServiceErrorKind.NotFound,
                HttpStatusCode.Unauthorized => DocumentServiceErrorKind.UpstreamUnauthorized,
                _ => DocumentServiceErrorKind.UpstreamError
            };

            throw new DocumentServiceException("Document service returned an error.", upstreamStatusCode: status, kind: kind);
        }

        var raw = await resp.Content.ReadAsStringAsync(ct);
        var downloadUrl = TryParseDownloadUrl(raw);

        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            _logger.LogWarning("DocumentService response missing url. OrgId: {OrgId}, DocumentId: {DocumentId}", orgId, documentId);
            throw new DocumentServiceException("Document service response missing url.", upstreamStatusCode: (int)resp.StatusCode, kind: DocumentServiceErrorKind.UpstreamError);
        }

        return downloadUrl;
    }

    public async Task<DocumentAnalysisEnvelopeDto> GetResumeAnalysisAsync(Guid orgId, Guid documentId, CancellationToken ct)
    {
        var baseUrl = _configuration["DocumentService:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new DocumentServiceException("DocumentService base URL is not configured.", upstreamStatusCode: null, kind: DocumentServiceErrorKind.Configuration);

        var url = $"{baseUrl.TrimEnd('/')}/api/NextHire/{orgId}/documents/{documentId}/analysis";

        using var client = _httpClientFactory.CreateClient("DocumentService");
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };

        HttpResponseMessage resp;
        try
        {
            resp = await client.SendAsync(request, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "DocumentService timeout (analysis). OrgId: {OrgId}, DocumentId: {DocumentId}", orgId, documentId);
            throw new DocumentServiceException("Document service timeout.", upstreamStatusCode: null, kind: DocumentServiceErrorKind.Timeout);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "DocumentService request failed (analysis). OrgId: {OrgId}, DocumentId: {DocumentId}", orgId, documentId);
            throw new DocumentServiceException("Document service request failed.", upstreamStatusCode: null, kind: DocumentServiceErrorKind.Network);
        }

        if (!resp.IsSuccessStatusCode)
        {
            var status = (int)resp.StatusCode;
            _logger.LogWarning("DocumentService non-success (analysis). OrgId: {OrgId}, DocumentId: {DocumentId}, Status: {Status}",
                orgId, documentId, status);

            var kind = resp.StatusCode switch
            {
                HttpStatusCode.NotFound => DocumentServiceErrorKind.NotFound,
                HttpStatusCode.Unauthorized => DocumentServiceErrorKind.UpstreamUnauthorized,
                _ => DocumentServiceErrorKind.UpstreamError
            };

            throw new DocumentServiceException("Document service returned an error.", upstreamStatusCode: status, kind: kind);
        }

        var raw = await resp.Content.ReadAsStringAsync(ct);
        var parsed = TryParseAnalysisEnvelope(raw);
        if (parsed is null || parsed.Analysis is null)
        {
            _logger.LogWarning("DocumentService response missing analysis. OrgId: {OrgId}, DocumentId: {DocumentId}", orgId, documentId);
            throw new DocumentServiceException("Document service response missing analysis.", upstreamStatusCode: (int)resp.StatusCode, kind: DocumentServiceErrorKind.UpstreamError);
        }

        return parsed;
    }

    private static string? TryParseDownloadUrl(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String)
                return urlProp.GetString();
        }
        catch
        {
            // ignore parse errors (handled by caller)
        }

        return null;
    }

    private static DocumentAnalysisEnvelopeDto? TryParseAnalysisEnvelope(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            return JsonSerializer.Deserialize<DocumentAnalysisEnvelopeDto>(raw, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }
}

public enum DocumentServiceErrorKind
{
    Configuration,
    Timeout,
    Network,
    NotFound,
    UpstreamUnauthorized,
    UpstreamError
}

public sealed class DocumentServiceException : Exception
{
    public int? UpstreamStatusCode { get; }
    public DocumentServiceErrorKind Kind { get; }

    public DocumentServiceException(string message, int? upstreamStatusCode, DocumentServiceErrorKind kind)
        : base(message)
    {
        UpstreamStatusCode = upstreamStatusCode;
        Kind = kind;
    }
}

