using nexthire_api.DTOs;

namespace nexthire_api.Services;

public interface IOrgProvisioningService
{
    Task<ProvisionOrganizationResponseDto> ProvisionAsync(
        ProvisionOrganizationRequestDto request,
        CancellationToken cancellationToken = default);
}
