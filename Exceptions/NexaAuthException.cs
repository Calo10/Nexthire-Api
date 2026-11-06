namespace nexthire_api.Exceptions;

public class NexaAuthException : Exception
{
    public int StatusCode { get; }
    public string ErrorMessage { get; }
    public string ResponseBody { get; }
    public string? Code { get; }

    public NexaAuthException(int statusCode, string errorMessage, string responseBody, string? code = null)
        : base($"Nexa auth error: {errorMessage}")
    {
        StatusCode = statusCode;
        ErrorMessage = errorMessage;
        ResponseBody = responseBody;
        Code = code;
    }
}
