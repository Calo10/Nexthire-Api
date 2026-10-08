namespace nexthire_api.Helpers;

/// <summary>
/// Local preview for the follow-up template that reopens a conversation after 24 hours.
/// Twilio renders the approved Content template. This text is stored only in whatsapp_messages.body.
/// </summary>
public static class WhatsAppFollowUpHistoryText
{
    public static string Format(string recruiterName, string candidateName, string organizationName)
    {
        return
            $"Hola {candidateName}, soy {recruiterName} de {organizationName}. " +
            "Seguimos con tu proceso de selección. Responde a este mensaje para continuar.";
    }
}
