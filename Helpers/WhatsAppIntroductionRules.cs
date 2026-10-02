using nexthire_api.Exceptions;

namespace nexthire_api.Helpers;

/// <summary>
/// Rules for the tenant-scoped WhatsApp introduction template.
/// Variable order is fixed: 1 recruiter, 2 candidate, 3 organization.
/// </summary>
public static class WhatsAppIntroductionRules
{
    public const string MissingContentSidMessage =
        "Twilio DefaultWhatsAppContentSid is not configured for this organization.";

    public static string RequireContentSid(string? contentSid)
    {
        if (string.IsNullOrWhiteSpace(contentSid))
            throw new WhatsAppIntroductionException(MissingContentSidMessage);

        return contentSid.Trim();
    }

    public static string? JoinPersonName(string? firstName, string? lastName)
    {
        var parts = new[] { firstName, lastName }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim());
        var name = string.Join(' ', parts);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    public static Dictionary<string, string> BuildVariables(
        string? recruiterName,
        string? candidateName,
        string? organizationName)
    {
        return new Dictionary<string, string>
        {
            ["1"] = RequireName(recruiterName, "Recruiter name is not available."),
            ["2"] = RequireName(candidateName, "Candidate name is not available."),
            ["3"] = RequireName(organizationName, "Organization name is not available.")
        };
    }

    /// <summary>
    /// Authenticated organization is the only tenant allowed to load the candidate and Twilio connection.
    /// </summary>
    public static void RejectForeignOrganization(Guid authenticatedOrgId, Guid resourceOrgId)
    {
        if (authenticatedOrgId == Guid.Empty || authenticatedOrgId != resourceOrgId)
            throw new UnauthorizedAccessException("Candidate is not in the current organization.");
    }

    public static string SafeTwilioConfigurationMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "Twilio is not configured for this organization.";

        if (message.Contains("AuthToken=", StringComparison.OrdinalIgnoreCase)
            || message.Contains("AccountSid=", StringComparison.OrdinalIgnoreCase))
        {
            return "Twilio is not configured for this organization.";
        }

        if (message.Contains("DefaultFromWhatsAppNumber", StringComparison.Ordinal))
            return "Twilio DefaultFromWhatsAppNumber is not configured for this organization.";

        if (message.Contains("not configured", StringComparison.OrdinalIgnoreCase))
            return message;

        return "Twilio is not configured for this organization.";
    }

    private static string RequireName(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new WhatsAppIntroductionException(message);

        return value.Trim();
    }
}
