namespace nexthire_api.WorkflowAutomation;

/// <summary>
/// Multi-tenant WhatsApp correlation helpers.
/// Messenger <c>tenantId</c> matches NextHire organization id (same pattern as LinkedIn Ads).
/// </summary>
public static class WhatsAppTenantResolver
{
    /// <summary>
    /// Resolves organization from Messenger inbound <c>tenantId</c>.
    /// </summary>
    public static bool TryResolveOrganizationId(string? messengerTenantId, out Guid organizationId)
    {
        organizationId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(messengerTenantId))
            return false;

        return Guid.TryParse(messengerTenantId.Trim(), out organizationId) && organizationId != Guid.Empty;
    }

    /// <summary>
    /// Builds a tenant-scoped conversation / correlation key for interviews.
    /// SendWhatsApp waits still use normalized phone; lookups always filter by organization id.
    /// </summary>
    public static string BuildConversationId(Guid organizationId, string normalizedPhone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedPhone);
        if (organizationId == Guid.Empty)
            throw new ArgumentException("OrganizationId is required.", nameof(organizationId));

        return $"{organizationId:D}:{normalizedPhone.Trim()}";
    }

    public static string NormalizePhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Trim();
        if (trimmed.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["whatsapp:".Length..].Trim();

        return trimmed.StartsWith('+') ? trimmed : $"+{trimmed.TrimStart('+')}";
    }
}
