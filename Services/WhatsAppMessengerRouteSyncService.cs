using nexthire_api.DTOs.Sourcing;
using nexthire_api.Helpers;
using nexthire_api.Repositories.Sourcing;

namespace nexthire_api.Services;

public class WhatsAppMessengerRouteSyncService : IWhatsAppMessengerRouteSyncService
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(750);

    private readonly ISourcingRepository _sourcing;
    private readonly INexaMessengerWhatsAppRoutesClient _routesClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WhatsAppMessengerRouteSyncService> _logger;

    public WhatsAppMessengerRouteSyncService(
        ISourcingRepository sourcing,
        INexaMessengerWhatsAppRoutesClient routesClient,
        IConfiguration configuration,
        ILogger<WhatsAppMessengerRouteSyncService> logger)
    {
        _sourcing = sourcing;
        _routesClient = routesClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<UpsertSourcingSourceConnectionResponseDto?> SyncTwilioRouteAfterUpsertAsync(
        Guid orgId,
        UpsertSourcingSourceConnectionRequestDto dto,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(dto.SourceTypeCode, "twilio", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "Skipping messenger route sync for sourceTypeCode {SourceTypeCode} (only twilio is synced).",
                dto.SourceTypeCode);
            return null;
        }

        if (!dto.IsConnected || !dto.IsActive)
        {
            _logger.LogInformation(
                "Skipping messenger route sync for org {OrgId}: isConnected={IsConnected}, isActive={IsActive}.",
                orgId,
                dto.IsConnected,
                dto.IsActive);

            await _sourcing.UpdateMessengerRouteSyncAsync(
                orgId,
                dto.SourceTypeCode,
                MessengerRouteSyncStatuses.NotRequired,
                null,
                null,
                cancellationToken);

            return BuildResponse(dto.SourceTypeCode, MessengerRouteSyncStatuses.NotRequired, null, null);
        }

        try
        {
            var twilio = TwilioSourceConnectionConfigParser.Parse(dto.ConfigJson ?? string.Empty);
            var inboundWebhookUrl = BuildInboundWebhookUrl();
            Exception? lastError = null;

            _logger.LogInformation(
                "Syncing WhatsApp inbound route to nexa-messenger for org {OrgId}. InboundWebhookUrl: {InboundWebhookUrl}",
                orgId,
                inboundWebhookUrl);

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    await _routesClient.RegisterRouteAsync(orgId, twilio, inboundWebhookUrl, cancellationToken);

                    var routeExists = await _routesClient.RouteExistsAsync(orgId, cancellationToken);
                    if (!routeExists)
                    {
                        _logger.LogDebug(
                            "nexa-messenger POST succeeded but GET route for org {OrgId} was not found (endpoint may be unimplemented or storage key differs).",
                            orgId);
                    }

                    var syncedAt = DateTimeOffset.UtcNow;
                    await _sourcing.UpdateMessengerRouteSyncAsync(
                        orgId,
                        dto.SourceTypeCode,
                        MessengerRouteSyncStatuses.Synced,
                        null,
                        syncedAt,
                        cancellationToken);

                    return BuildResponse(dto.SourceTypeCode, MessengerRouteSyncStatuses.Synced, null, syncedAt);
                }
                catch (Exception ex) when (attempt < MaxAttempts)
                {
                    lastError = ex;
                    _logger.LogWarning(
                        ex,
                        "WhatsApp route registration attempt {Attempt}/{MaxAttempts} failed for org {OrgId}. Retrying.",
                        attempt,
                        MaxAttempts,
                        orgId);
                    await Task.Delay(RetryDelay * attempt, cancellationToken);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            var errorMessage = TruncateError(lastError?.Message ?? "WhatsApp route registration failed.");
            await _sourcing.UpdateMessengerRouteSyncAsync(
                orgId,
                dto.SourceTypeCode,
                MessengerRouteSyncStatuses.Pending,
                errorMessage,
                null,
                cancellationToken);

            _logger.LogError(
                lastError,
                "WhatsApp route registration failed for org {OrgId} after {MaxAttempts} attempts. Marked as pending sync.",
                orgId,
                MaxAttempts);

            return BuildResponse(dto.SourceTypeCode, MessengerRouteSyncStatuses.Pending, errorMessage, null);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            await _sourcing.UpdateMessengerRouteSyncAsync(
                orgId,
                dto.SourceTypeCode,
                MessengerRouteSyncStatuses.Pending,
                TruncateError(ex.Message),
                null,
                cancellationToken);

            return BuildResponse(dto.SourceTypeCode, MessengerRouteSyncStatuses.Pending, TruncateError(ex.Message), null);
        }
    }

    private string BuildInboundWebhookUrl() =>
        WhatsAppInboundWebhookUrlResolver.Resolve(_configuration);

    private static UpsertSourcingSourceConnectionResponseDto BuildResponse(
        string sourceTypeCode,
        string status,
        string? error,
        DateTimeOffset? syncedAt)
    {
        return new UpsertSourcingSourceConnectionResponseDto
        {
            SourceTypeCode = sourceTypeCode,
            MessengerRouteSyncStatus = status,
            MessengerRouteSyncError = error,
            MessengerRouteSyncedAt = syncedAt
        };
    }

    private static string TruncateError(string message)
    {
        const int maxLength = 2000;
        if (message.Length <= maxLength)
            return message;
        return message[..maxLength];
    }
}
