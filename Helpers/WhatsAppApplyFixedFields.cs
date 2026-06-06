using nexthire_api.DTOs;

namespace nexthire_api.Helpers;

public static class WhatsAppApplyFixedFields
{
    public static string GetPrompt(string fieldKey, bool isFirstQuestion, string language) =>
        WhatsAppBotMessages.FixedFieldPrompt(language, fieldKey, isFirstQuestion);

    public static string GetRepeatPrompt(string fieldKey, string language) =>
        WhatsAppBotMessages.FixedFieldRepeatPrompt(language, fieldKey);

    public static string? GetCurrentFieldKey(WhatsAppApplySessionState session)
    {
        if (session.CurrentFixedFieldIndex < 0 || session.CurrentFixedFieldIndex >= WhatsAppApplyFixedFieldKeys.Order.Length)
            return null;

        return WhatsAppApplyFixedFieldKeys.Order[session.CurrentFixedFieldIndex];
    }

    public static void SetFieldValue(WhatsAppApplySessionState session, string fieldKey, string value)
    {
        switch (fieldKey)
        {
            case WhatsAppApplyFixedFieldKeys.FirstName:
                session.FirstName = value;
                break;
            case WhatsAppApplyFixedFieldKeys.LastName:
                session.LastName = value;
                SyncFullName(session);
                break;
            case WhatsAppApplyFixedFieldKeys.Email:
                session.Email = value;
                break;
        }
    }

    /// <summary>Builds <see cref="WhatsAppApplySessionState.FullName"/> from first and last name.</summary>
    public static void SyncFullName(WhatsAppApplySessionState session)
    {
        var parts = new[] { session.FirstName, session.LastName }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim())
            .ToArray();

        session.FullName = parts.Length > 0 ? string.Join(' ', parts) : null;
    }
}
