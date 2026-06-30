namespace nexthire_api.Services;

public interface IWhatsAppTwilioMediaService
{
    /// <summary>
    /// Downloads a Twilio-hosted media URL (requires Basic auth).
    /// </summary>
    Task<WhatsAppTwilioMediaDownload> DownloadAsync(
        Guid orgId,
        string mediaUrl,
        string? fileNameHint = null,
        CancellationToken cancellationToken = default);
}

public sealed class WhatsAppTwilioMediaDownload : IAsyncDisposable
{
    public required Stream Stream { get; init; }
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required long Length { get; init; }

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync();
    }
}
