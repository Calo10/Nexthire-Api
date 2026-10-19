namespace nexthire_api.Exceptions;

public class PipelineStageException : Exception
{
    public int StatusCode { get; }
    public string Code { get; }

    public PipelineStageException(int statusCode, string code, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }
}
