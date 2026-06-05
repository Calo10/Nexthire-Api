using nexthire_api.DTOs;

namespace nexthire_api.Helpers;

public static class WhatsAppInboundFileHelper
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx"
    };

    public static string? TryGetResumeFileNameFromBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        var trimmed = Uri.UnescapeDataString(body.Trim().Replace('+', ' '));
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return null;

        var fileName = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var ext = Path.GetExtension(fileName);
        return AllowedExtensions.Contains(ext) ? fileName : null;
    }

    public static bool TryGetInlineMedia(
        WhatsAppInboundMessageDto inbound,
        out byte[] content,
        out string fileName,
        out string contentType)
    {
        content = Array.Empty<byte>();
        fileName = string.Empty;
        contentType = "application/octet-stream";

        WhatsAppInboundMediaHelper.EnsureMediaPopulated(inbound);
        var bodyFileName = TryGetResumeFileNameFromBody(inbound.Body);

        foreach (var item in inbound.Media ?? [])
        {
            if (string.IsNullOrWhiteSpace(item.ContentBase64))
                continue;

            try
            {
                content = Convert.FromBase64String(item.ContentBase64.Trim());
            }
            catch (FormatException)
            {
                continue;
            }

            contentType = string.IsNullOrWhiteSpace(item.ResolvedMimeType)
                ? GuessContentType(item.FileName ?? bodyFileName)
                : item.ResolvedMimeType!;

            fileName = ResolveUploadFileName(item.FileName, bodyFileName, contentType);
            return content.Length > 0;
        }

        return false;
    }

    private static string ResolveUploadFileName(string? mediaFileName, string? bodyFileName, string contentType)
    {
        var candidate = !string.IsNullOrWhiteSpace(mediaFileName)
            ? Path.GetFileName(mediaFileName.Trim())
            : bodyFileName;

        if (!string.IsNullOrWhiteSpace(candidate))
        {
            var ext = Path.GetExtension(candidate);
            if (AllowedExtensions.Contains(ext))
                return candidate;
        }

        var fromMime = ExtensionFromContentType(contentType);
        return "resume" + (fromMime ?? ".pdf");
    }

    private static string GuessContentType(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "application/pdf",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            _ => "application/octet-stream"
        };
    }

    private static string? ExtensionFromContentType(string contentType) =>
        contentType.Trim().ToLowerInvariant() switch
        {
            "application/pdf" => ".pdf",
            "application/msword" => ".doc",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
            _ => null
        };
}
