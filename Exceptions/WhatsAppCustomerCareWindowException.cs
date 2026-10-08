namespace nexthire_api.Exceptions;

public sealed class WhatsAppCustomerCareWindowException : Exception
{
    public WhatsAppCustomerCareWindowException()
        : base(Helpers.WhatsAppCustomerCareWindow.ClosedMessage)
    {
    }
}
