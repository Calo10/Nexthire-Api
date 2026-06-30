using nexthire_api.DTOs.Sourcing;

namespace nexthire_api.Services;

public interface IWhatsAppMessengerRouteSyncService
{
    Task<UpsertSourcingSourceConnectionResponseDto?> SyncTwilioRouteAfterUpsertAsync(
        Guid orgId,
        UpsertSourcingSourceConnectionRequestDto dto,
        CancellationToken cancellationToken = default);
}
