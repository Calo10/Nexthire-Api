namespace nexthire_api.Exceptions;

public sealed class WhatsAppIntroductionException : Exception
{
    public int StatusCode { get; }

    public WhatsAppIntroductionException(string message, int statusCode = 400)
        : base(message)
    {
        StatusCode = statusCode;
    }
}
