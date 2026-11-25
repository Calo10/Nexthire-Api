namespace nexthire_api.Services;

public static class OrganizationPositionStatuses
{
    public const string Active = "Active";
    public const string Planned = "Planned";
    public const string Frozen = "Frozen";
    public const string Archived = "Archived";

    public static readonly IReadOnlyList<string> All = [Active, Planned, Frozen, Archived];
}

public static class OrganizationLocationTypes
{
    public const string Office = "Office";
    public const string Remote = "Remote";
    public const string Hybrid = "Hybrid";
    public const string ClientSite = "ClientSite";
    public const string Other = "Other";

    public static readonly IReadOnlyList<string> All = [Office, Remote, Hybrid, ClientSite, Other];
}

public static class OrganizationAssignmentStatuses
{
    public const string Active = "Active";
    public const string Ended = "Ended";
    public const string Planned = "Planned";

    public static readonly IReadOnlyList<string> All = [Active, Ended, Planned];
}

public static class OrganizationDefaultLevels
{
    public static readonly (string Name, int Order, string? Description)[] Defaults =
    [
        ("Executive", 1, "C-level and equivalent"),
        ("Director", 2, "Directors and VPs"),
        ("Manager", 3, "People managers"),
        ("Supervisor", 4, "Team supervisors"),
        ("Individual Contributor", 5, "IC roles"),
        ("Associate", 6, "Entry and associate roles")
    ];
}

public static class OrganizationDeptIcons
{
    public static readonly string[] Known =
    [
        "operations", "technology", "people", "sales", "finance", "marketing",
        "hospitality", "administration", "default"
    ];
}

public static class OrganizationValidation
{
    public static string? NormalizePositionStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return OrganizationPositionStatuses.All.FirstOrDefault(s =>
            s.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static string? NormalizeLocationType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return OrganizationLocationTypes.All.FirstOrDefault(s =>
            s.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizeIcon(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "default";
        var v = value.Trim().ToLowerInvariant();
        return OrganizationDeptIcons.Known.Contains(v) ? v : "default";
    }

    public static string? NormalizeColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (v.StartsWith('#') && (v.Length == 7 || v.Length == 4))
            return v.ToUpperInvariant();
        return null;
    }
}
