using nexthire_api.DTOs;

namespace nexthire_api.Helpers;

public static class WhatsAppResumeUploadDiagnostics
{
    public static string DescribeInboundMedia(WhatsAppInboundMessageDto inbound)
    {
        WhatsAppInboundMediaHelper.EnsureMediaPopulated(inbound);
        var count = inbound.Media?.Count ?? 0;
        if (count == 0)
            return "mediaCount=0";

        var first = inbound.Media![0];
        var hasUrl = !string.IsNullOrWhiteSpace(first.Url);
        var hasInline = !string.IsNullOrWhiteSpace(first.ContentBase64);
        var inlineBytes = 0;
        if (hasInline)
        {
            try
            {
                inlineBytes = Convert.FromBase64String(first.ContentBase64!.Trim()).Length;
            }
            catch
            {
                inlineBytes = -1;
            }
        }

        return $"mediaCount={count} hasUrl={hasUrl} hasInline={hasInline} inlineBytes={inlineBytes} mime={first.ResolvedMimeType ?? "(none)"}";
    }

    public static string ClassifyUploadFailure(Exception ex) =>
        ex switch
        {
            InvalidOperationException { Message: var m } when m.Contains("Twilio credentials", StringComparison.OrdinalIgnoreCase)
                || m.Contains("Twilio is not configured", StringComparison.OrdinalIgnoreCase)
                => "twilio_credentials_missing",
            InvalidOperationException { Message: var m } when m.Contains("Failed to download WhatsApp media", StringComparison.OrdinalIgnoreCase)
                => "twilio_download_failed",
            InvalidOperationException { Message: var m } when m.Contains("documents endpoint not found", StringComparison.OrdinalIgnoreCase)
                => "documents_wrong_host_or_not_running",
            InvalidOperationException { Message: var m } when m.Contains("Failed to upload resume document (status 401)", StringComparison.OrdinalIgnoreCase)
                => "documents_unauthorized",
            InvalidOperationException { Message: var m } when m.Contains("Failed to upload resume document", StringComparison.OrdinalIgnoreCase)
                => "documents_upload_failed",
            InvalidOperationException { Message: var m } when m.Contains("resume file type not allowed", StringComparison.OrdinalIgnoreCase)
                => "invalid_file_type",
            InvalidOperationException { Message: var m } when m.Contains("too large", StringComparison.OrdinalIgnoreCase)
                => "file_too_large",
            _ => "unknown"
        };
}
