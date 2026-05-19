using Microsoft.Extensions.Configuration;

namespace nexthire_api.Helpers;

/// <summary>
/// Maps messenger / Twilio pipeline tenant labels to the NextHire org tenant id stored in DB
/// (so inbound webhooks attach to the same whatsapp_conversations row as send-direct / UI).
/// </summary>
public static class WhatsAppTenantResolver
{
    private const string AliasesSectionPath = "WhatsApp:InboundTenantAliases";

    public static string Resolve(IConfiguration configuration, string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return tenantId;

        var trimmed = tenantId.Trim();
        var section = configuration.GetSection(AliasesSectionPath);
        foreach (var child in section.GetChildren())
        {
            if (!string.Equals(child.Key, trimmed, StringComparison.OrdinalIgnoreCase))
                continue;
            var mapped = child.Value;
            if (!string.IsNullOrWhiteSpace(mapped))
                return mapped.Trim();
        }

        return trimmed;
    }
}
