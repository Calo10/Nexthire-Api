using nexthire_api.DTOs;
using nexthire_api.Helpers;

namespace nexthire_api.Services;

public class WhatsAppApplyResumeService : IWhatsAppApplyResumeService
{
    private readonly IWhatsAppTwilioMediaService _twilioMedia;
    private readonly IResumeDocumentsUploader _resumeUploader;
    private readonly ILogger<WhatsAppApplyResumeService> _logger;

    public WhatsAppApplyResumeService(
        IWhatsAppTwilioMediaService twilioMedia,
        IResumeDocumentsUploader resumeUploader,
        ILogger<WhatsAppApplyResumeService> logger)
    {
        _twilioMedia = twilioMedia;
        _resumeUploader = resumeUploader;
        _logger = logger;
    }

    public async Task<string> UploadResumeFromInboundAsync(
        Guid orgId,
        WhatsAppInboundMessageDto inbound,
        CancellationToken cancellationToken = default)
    {
        WhatsAppInboundMediaHelper.EnsureMediaPopulated(inbound);
        _logger.LogInformation(
            "[WhatsAppResume] Step=validate_inbound OrgId={OrgId} {MediaDiagnostics}",
            orgId,
            WhatsAppResumeUploadDiagnostics.DescribeInboundMedia(inbound));

        if (WhatsAppInboundFileHelper.TryGetInlineMedia(inbound, out var content, out var fileName, out var contentType))
        {
            _logger.LogInformation(
                "[WhatsAppResume] Step=upload_inline OrgId={OrgId} FileName={FileName} Bytes={Bytes} ContentType={ContentType}",
                orgId,
                fileName,
                content.Length,
                contentType);

            await using var stream = new MemoryStream(content, writable: false);
            return await _resumeUploader.UploadResumeAsync(
                orgId,
                stream,
                fileName,
                contentType,
                content.Length,
                cancellationToken);
        }

        var mediaUrl = WhatsAppDynamicApplyHelper.TryGetFileAnswerUrl(inbound);
        if (string.IsNullOrWhiteSpace(mediaUrl))
            throw new InvalidOperationException("No WhatsApp media URL found in inbound message.");

        _logger.LogInformation(
            "[WhatsAppResume] Step=download_twilio OrgId={OrgId} MediaUrlHost={Host}",
            orgId,
            Uri.TryCreate(mediaUrl, UriKind.Absolute, out var mediaUri) ? mediaUri.Host : "(invalid)");

        var fileNameHint = WhatsAppInboundFileHelper.TryGetResumeFileNameFromBody(inbound.Body);
        return await UploadFromRemoteUrlAsync(orgId, mediaUrl, fileNameHint, cancellationToken);
    }

    public async Task<string?> EnsureResumeDocumentIdAsync(
        Guid orgId,
        string? value,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (Guid.TryParse(trimmed, out _))
            return trimmed;

        if (!IsRemoteResumeUrl(trimmed))
            return trimmed;

        return await UploadFromRemoteUrlAsync(orgId, trimmed, fileNameHint: null, cancellationToken);
    }

    private async Task<string> UploadFromRemoteUrlAsync(
        Guid orgId,
        string mediaUrl,
        string? fileNameHint,
        CancellationToken cancellationToken)
    {
        await using var download = await _twilioMedia.DownloadAsync(mediaUrl, fileNameHint, cancellationToken);
        _logger.LogInformation(
            "[WhatsAppResume] Step=upload_documents OrgId={OrgId} FileName={FileName} Bytes={Bytes} ContentType={ContentType}",
            orgId,
            download.FileName,
            download.Length,
            download.ContentType);

        var documentId = await _resumeUploader.UploadResumeAsync(
            orgId,
            download.Stream,
            download.FileName,
            download.ContentType,
            download.Length,
            cancellationToken);

        _logger.LogInformation(
            "[WhatsAppResume] Step=complete OrgId={OrgId} DocumentId={DocumentId}",
            orgId,
            documentId);

        return documentId;
    }

    private static bool IsRemoteResumeUrl(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
