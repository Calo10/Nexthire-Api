using System.Net.Mail;

namespace nexthire_api.WorkflowAutomation;

/// <summary>
/// Multi-tenant email correlation. Messenger <c>tenantId</c> = organization Guid (same as WhatsApp).
/// </summary>
public static class EmailTenantResolver
{
    public static bool TryResolveOrganizationId(string? messengerTenantId, out Guid organizationId) =>
        WhatsAppTenantResolver.TryResolveOrganizationId(messengerTenantId, out organizationId);

    public static string BuildConversationId(Guid organizationId, string normalizedEmail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedEmail);
        if (organizationId == Guid.Empty)
            throw new ArgumentException("OrganizationId is required.", nameof(organizationId));

        return $"{organizationId:D}:{normalizedEmail.Trim()}";
    }

    public static string NormalizeEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var candidate = value.Trim();
        var start = candidate.LastIndexOf('<');
        var end = candidate.LastIndexOf('>');
        if (start >= 0 && end > start)
            candidate = candidate[(start + 1)..end];

        candidate = candidate.Split(',')[0].Trim().Trim('"');
        try
        {
            var addr = new MailAddress(candidate);
            return addr.Address.Trim().ToLowerInvariant();
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }
}
