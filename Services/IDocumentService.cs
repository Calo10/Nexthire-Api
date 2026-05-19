namespace nexthire_api.Services;

using nexthire_api.DTOs;

public interface IDocumentService
{
    Task<string> GetResumeDownloadUrlAsync(Guid orgId, Guid documentId, CancellationToken ct);
    Task<DocumentAnalysisEnvelopeDto> GetResumeAnalysisAsync(Guid orgId, Guid documentId, CancellationToken ct);
}

