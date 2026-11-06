using nexthire_api.Repositories;

namespace nexthire_api.Security;

/// <summary>
/// Users whose only NextHire role is Sales may read dashboard, jobs, candidates, and pipeline.
/// Any other role, or no role, keeps the existing access.
/// </summary>
public class SalesAccessMiddleware
{
    private static readonly string[] ReadPrefixes =
    [
        "/api/dashboard",
        "/api/jobs",
        "/api/candidates",
        "/api/applications",
        "/api/pipeline",
        "/api/documents",
        "/api/notes"
    ];

    private static readonly string[] ReadExact =
    [
        "/api/me",
        "/api/org/users/me",
        "/api/org/settings",
        "/api/org/settings/palettes",
        "/api/org/settings/pipeline-stages",
        "/api/auth/organizations"
    ];

    private readonly RequestDelegate _next;

    public SalesAccessMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IRoleRepository roles)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await _next(context);
            return;
        }

        if (!TryGetScope(context.User, out var orgId, out var nexaUserId))
        {
            await _next(context);
            return;
        }

        var codes = await roles.ListRoleCodesByNexaUserAsync(orgId, nexaUserId);
        if (!IsSalesReadOnly(codes))
        {
            await _next(context);
            return;
        }

        if (IsAllowed(context.Request))
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new
        {
            message = "This role can only view dashboard, jobs, candidates, and pipeline.",
            code = "read_only"
        });
    }

    public static bool IsSalesReadOnly(IEnumerable<string> codes)
    {
        var normalized = codes
            .Select(code => code.Trim().ToLowerInvariant())
            .Where(code => code.Length > 0)
            .Distinct()
            .ToList();
        return normalized.Count > 0 && normalized.All(code => code == "sales");
    }

    private static bool IsAllowed(HttpRequest request)
    {
        var path = request.Path.Value ?? string.Empty;
        if (request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
            && (path.Equals("/api/logout", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/api/auth/switch-organization", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/api/candidates/public-links", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (request.Method.Equals("DELETE", StringComparison.OrdinalIgnoreCase)
            && path.StartsWith("/api/candidates/public-links/", StringComparison.OrdinalIgnoreCase))
            return true;

        if (request.Method is not ("GET" or "HEAD" or "OPTIONS"))
            return false;

        if (ReadExact.Any(exact => path.Equals(exact, StringComparison.OrdinalIgnoreCase)))
            return true;

        return ReadPrefixes.Any(prefix =>
            path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetScope(System.Security.Claims.ClaimsPrincipal user, out Guid orgId, out Guid nexaUserId)
    {
        orgId = default;
        nexaUserId = default;
        try
        {
            orgId = ClaimUtils.RequireOrgId(user);
            nexaUserId = ClaimUtils.RequireNexaUserId(user);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
