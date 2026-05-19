namespace nexthire_api.Exceptions;

public class NexaAuthException : Exception
{
    public int StatusCode { get; }
    public string ErrorMessage { get; }
    public string ResponseBody { get; }

    public NexaAuthException(int statusCode, string errorMessage, string responseBody)
        : base($"Nexa auth error: {errorMessage}")
    {
        StatusCode = statusCode;
        ErrorMessage = errorMessage;
        ResponseBody = responseBody;
    }
}
