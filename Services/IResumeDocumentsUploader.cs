using Microsoft.AspNetCore.Http;

namespace nexthire_api.Services;

/// <summary>
/// Uploads resume files to the Documents Azure Function and returns the stored document id.
/// </summary>
public interface IResumeDocumentsUploader
{
    /// <exception cref="ArgumentException">When the file is missing or empty.</exception>
    /// <exception cref="InvalidOperationException">Validation, configuration, or upstream upload failures.</exception>
    Task<string> UploadResumeAsync(Guid orgId, IFormFile resume, CancellationToken cancellationToken = default);

    /// <exception cref="ArgumentException">When the stream is missing or empty.</exception>
    /// <exception cref="InvalidOperationException">Validation, configuration, or upstream upload failures.</exception>
    Task<string> UploadResumeAsync(
        Guid orgId,
        Stream content,
        string fileName,
        string? contentType,
        long length,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Same upload as <see cref="UploadResumeAsync(Guid, Stream, string, string?, long, CancellationToken)"/> but with an
    /// explicit extension allowlist (e.g. <c>.csv</c> for generated Core HR report exports). Resume endpoints keep the
    /// pdf/doc/docx allowlist.
    /// </summary>
    /// <exception cref="ArgumentException">When the stream is missing or empty.</exception>
    /// <exception cref="InvalidOperationException">Validation, configuration, or upstream upload failures.</exception>
    Task<string> UploadDocumentAsync(
        Guid orgId,
        Stream content,
        string fileName,
        string? contentType,
        long length,
        IReadOnlySet<string> allowedExtensions,
        CancellationToken cancellationToken = default);
}
