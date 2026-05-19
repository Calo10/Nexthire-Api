using System.Security.Claims;

namespace nexthire_api.Security;

public static class ClaimUtils
{
    /// <summary>
    /// Require org scope for business endpoints.
    /// Reads the "org_id" claim and parses it as a GUID.
    /// </summary>
    public static Guid RequireOrgId(ClaimsPrincipal user)
    {
        // Prefer "org_id" (canonical), but accept legacy claim names for compatibility.
        var value = user.FindFirst("org_id")?.Value
                    ?? user.FindFirst("orgId")?.Value
                    ?? user.FindFirst("nexa_org_id")?.Value;

        if (string.IsNullOrWhiteSpace(value) || !Guid.TryParse(value, out var orgId))
            throw new UnauthorizedAccessException("Missing or invalid org_id claim.");

        return orgId;
    }

    /// <summary>
    /// Require Nexa user id for endpoints that need nh_users linkage.
    /// Prefers "nexa_user_id", falls back to "sub".
    /// </summary>
    public static Guid RequireNexaUserId(ClaimsPrincipal user)
    {
        var value = user.FindFirst("nexa_user_id")?.Value
                    ?? user.FindFirst("sub")?.Value
                    ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrWhiteSpace(value) || !Guid.TryParse(value, out var nexaUserId))
            throw new UnauthorizedAccessException("Missing or invalid nexa_user_id claim.");

        return nexaUserId;
    }

    public static string? GetEmail(ClaimsPrincipal user)
    {
        var value = user.FindFirst(ClaimTypes.Email)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static string? GetDisplayName(ClaimsPrincipal user)
    {
        // Common claim names if you ever add them later.
        var value = user.FindFirst("name")?.Value
                    ?? user.FindFirst("display_name")?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Require authenticated user id.
    /// Reads the standard JWT "sub" claim and parses it as a GUID.
    /// </summary>
    public static Guid RequireUserId(ClaimsPrincipal user)
    {
        // Depending on JWT inbound claim mapping, "sub" may appear as ClaimTypes.NameIdentifier.
        var value = user.FindFirst("sub")?.Value
                    ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrWhiteSpace(value) || !Guid.TryParse(value, out var userId))
            throw new UnauthorizedAccessException("Missing or invalid sub claim.");

        return userId;
    }

    /// <summary>
    /// Optional role claim.
    /// Reads the "role" claim, if present.
    /// </summary>
    public static string? GetRole(ClaimsPrincipal user)
    {
        // Depending on inbound claim mapping, "role" may appear as ClaimTypes.Role.
        var value = user.FindFirst("role")?.Value
                    ?? user.FindFirst(ClaimTypes.Role)?.Value;

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

