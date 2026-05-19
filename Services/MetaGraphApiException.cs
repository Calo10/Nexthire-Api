namespace nexthire_api.Services;

/// <summary>Thrown when the Meta Graph API returns an error response.</summary>
public sealed class MetaGraphApiException : Exception
{
    public int HttpStatus { get; }
    public int? MetaCode { get; }
    public string? MetaErrorUserTitle { get; }
    public string? MetaErrorUserMsg { get; }

    public MetaGraphApiException(
        string message,
        int httpStatus,
        int? metaCode = null,
        string? metaErrorUserTitle = null,
        string? metaErrorUserMsg = null,
        Exception? inner = null)
        : base(message, inner)
    {
        HttpStatus = httpStatus;
        MetaCode = metaCode;
        MetaErrorUserTitle = metaErrorUserTitle;
        MetaErrorUserMsg = metaErrorUserMsg;
    }
}
