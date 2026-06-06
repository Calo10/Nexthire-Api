using nexthire_api.DTOs;

namespace nexthire_api.Services;

/// <summary>
/// Downloads WhatsApp/Twilio media and uploads it through the Documents pipeline (same as sourcing leads).
/// </summary>
public interface IWhatsAppApplyResumeService
{
    /// <returns>Documents function document id (stored in sourcing_leads.resume_url).</returns>
    Task<string> UploadResumeFromInboundAsync(
        Guid orgId,
        WhatsAppInboundMessageDto inbound,
        CancellationToken cancellationToken = default);

    /// <returns>Document id when <paramref name="value"/> is a remote media URL; otherwise the original value.</returns>
    Task<string?> EnsureResumeDocumentIdAsync(
        Guid orgId,
        string? value,
        CancellationToken cancellationToken = default);
}
