namespace nexthire_api.Helpers;

/// <summary>
/// History preview for an introduction template. Twilio renders the approved Content template
/// from ContentSid and ContentVariables. This text is stored only in whatsapp_messages.body
/// so the existing conversation UI can show the outreach. It is not sent to Twilio.
/// </summary>
public static class WhatsAppIntroductionHistoryText
{
    public static string Format(string recruiterName, string candidateName, string organizationName)
    {
        return
            $"Hola {candidateName}, soy {recruiterName} de {organizationName}. " +
            "Nos comunicamos contigo porque tenemos una oportunidad laboral que podría ser de tu interés. " +
            "¿Te gustaría recibir más información?";
    }
}
