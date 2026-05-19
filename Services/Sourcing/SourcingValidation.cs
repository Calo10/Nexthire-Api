using System.Net.Mail;
using nexthire_api.DTOs.Sourcing;
using nexthire_api.Sourcing;

namespace nexthire_api.Services.Sourcing;

internal static class SourcingValidation
{
    public static (int Page, int PageSize) NormalizePaging(int page, int pageSize)
    {
        var p = page < 1 ? SourcingConstants.DefaultPage : page;
        var ps = pageSize < 1 ? SourcingConstants.DefaultPageSize : pageSize;
        if (ps > SourcingConstants.MaxPageSize)
            ps = SourcingConstants.MaxPageSize;
        return (p, ps);
    }

    public static void EnsureLeadIdentity(CreateSourcingLeadRequestDto dto)
    {
        var hasName = !string.IsNullOrWhiteSpace(dto.FullName)
                      || (!string.IsNullOrWhiteSpace(dto.FirstName) && !string.IsNullOrWhiteSpace(dto.LastName));
        var hasContact = !string.IsNullOrWhiteSpace(dto.Email) || !string.IsNullOrWhiteSpace(dto.Phone);

        if (!hasName && !hasContact)
            throw new ArgumentException("Provide full name, first and last name, or at least email or phone.");
    }

    public static void ValidateOptionalEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return;

        try
        {
            _ = new MailAddress(email);
        }
        catch
        {
            throw new ArgumentException("Invalid email format.");
        }
    }

    public static void ValidateFitScore(decimal? fitScore)
    {
        if (!fitScore.HasValue)
            return;
        if (fitScore.Value < 0 || fitScore.Value > 100)
            throw new ArgumentException("fitScore must be between 0 and 100.");
    }

    public static void ValidateLeadStatus(string status)
    {
        if (!SourcingConstants.LeadStatuses.Contains(status, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid lead status.");
    }

    public static void ValidateCampaignStatus(string status)
    {
        if (!SourcingConstants.CampaignStatuses.Contains(status, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid campaign status.");
    }

    public static void ValidateTrackingEventType(string eventType)
    {
        if (!SourcingConstants.TrackingEventTypes.Contains(eventType, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid tracking event type.");
    }

    public static string? NormalizePhoneDigits(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return null;
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? null : digits;
    }

    public static string NormalizeEmail(string? email)
    {
        return string.IsNullOrWhiteSpace(email) ? string.Empty : email.Trim().ToLowerInvariant();
    }
}
