namespace nexthire_api.Exceptions;

/// <summary>Business-rule failure in Core HR with an HTTP status hint (400 validation, 404 missing, 409 conflict).</summary>
public sealed class EmployeeServiceException : Exception
{
    public int StatusCode { get; }
    public string Code { get; }

    public EmployeeServiceException(int statusCode, string code, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public static EmployeeServiceException Validation(string message, string code = "validation_failed") => new(400, code, message);
    public static EmployeeServiceException NotFound(string message) => new(404, "not_found", message);
    public static EmployeeServiceException Conflict(string message, string code = "conflict") => new(409, code, message);
}

/// <summary>The caller's roles do not grant the required Core HR capability (maps to HTTP 403).</summary>
public sealed class HrAccessDeniedException : UnauthorizedAccessException
{
    public string Capability { get; }

    public HrAccessDeniedException(string capability)
        : base($"Your role does not allow this action ({capability}).")
    {
        Capability = capability;
    }
}
