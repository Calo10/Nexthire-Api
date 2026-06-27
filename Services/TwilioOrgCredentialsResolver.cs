using nexthire_api.Helpers;
using nexthire_api.Options;
using nexthire_api.Repositories.Sourcing;

namespace nexthire_api.Services;

public class TwilioOrgCredentialsResolver : ITwilioOrgCredentialsResolver
{
    public const string TwilioSourceTypeCode = "twilio";

    private readonly ISourcingRepository _sourcing;

    public TwilioOrgCredentialsResolver(ISourcingRepository sourcing)
    {
        _sourcing = sourcing;
    }

    public async Task<TwilioOrgCredentials> ResolveAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var configJson = await _sourcing.GetActiveSourceConnectionConfigJsonAsync(
            orgId,
            TwilioSourceTypeCode,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(configJson))
        {
            throw new InvalidOperationException(
                $"Twilio is not configured for this organization. Connect source '{TwilioSourceTypeCode}' " +
                "with isConnected=true, isActive=true, and valid credentials.");
        }

        try
        {
            return TwilioSourceConnectionConfigParser.Parse(configJson);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"Twilio connection config_json is invalid for source '{TwilioSourceTypeCode}': {ex.Message}",
                ex);
        }
    }
}
