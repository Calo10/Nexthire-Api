using nexthire_api.DTOs;

namespace nexthire_api.Services;

public interface IAdminAccountService
{
    Task<CreateAdminAccountResponseDto> CreateAdminAccountAsync(
        CreateAdminAccountRequestDto request,
        CancellationToken cancellationToken = default);
}
