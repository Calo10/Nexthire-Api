using Microsoft.AspNetCore.Mvc;
using nexthire_api.Exceptions;
using nexthire_api.Security;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

/// <summary>Shared actor resolution and exception mapping for Core HR controllers.</summary>
public abstract class CoreHrControllerBase : ControllerBase
{
    private readonly ILogger _logger;

    protected CoreHrControllerBase(ILogger logger)
    {
        _logger = logger;
    }

    protected HrActor Actor() => new(
        ClaimUtils.RequireOrgId(User),
        ClaimUtils.RequireNexaUserId(User),
        ClaimUtils.GetDisplayName(User) ?? ClaimUtils.GetEmail(User));

    /// <summary>Runs an action and maps service/auth exceptions to HTTP responses.</summary>
    protected async Task<IActionResult> RunAsync(Func<HrActor, Task<IActionResult>> action, string operation)
    {
        try
        {
            return await action(Actor());
        }
        catch (HrAccessDeniedException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message, code = "forbidden" });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (EmployeeServiceException ex)
        {
            return StatusCode(ex.StatusCode, new { message = ex.Message, code = ex.Code });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Core HR operation: {Operation}", operation);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while processing the request." });
        }
    }

    protected static NotFoundObjectResult NotFoundResult(string what) => new(new { message = $"{what} not found." });
}
