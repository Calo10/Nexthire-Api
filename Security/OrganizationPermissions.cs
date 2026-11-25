namespace nexthire_api.Security;

/// <summary>
/// Core HR Organization capability strings mapped onto existing NextHire role codes.
/// No parallel ACL tables — mirrors <see cref="OffboardingPermissions"/>.
/// </summary>
public static class OrganizationPermissions
{
    public const string View = "Organization.View";
    public const string ViewPeople = "Organization.ViewPeople";
    public const string ManageDepartments = "Organization.ManageDepartments";
    public const string ManagePositions = "Organization.ManagePositions";
    public const string ManageReporting = "Organization.ManageReporting";
    public const string ManageLocations = "Organization.ManageLocations";
    public const string ManageLevels = "Organization.ManageLevels";
    public const string ManageHeadcount = "Organization.ManageHeadcount";
    public const string AssignEmployees = "Organization.AssignEmployees";
    public const string TransferEmployees = "Organization.TransferEmployees";
    public const string ViewAnalytics = "Organization.ViewAnalytics";
    public const string ViewHistory = "Organization.ViewHistory";
    public const string Import = "Organization.Import";
    public const string Export = "Organization.Export";
    public const string ManageSettings = "Organization.ManageSettings";

    public static readonly IReadOnlyList<string> All =
    [
        View, ViewPeople, ManageDepartments, ManagePositions, ManageReporting,
        ManageLocations, ManageLevels, ManageHeadcount, AssignEmployees, TransferEmployees,
        ViewAnalytics, ViewHistory, Import, Export, ManageSettings
    ];

    private static readonly Dictionary<string, HashSet<string>> RoleCapabilities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["account_admin"] = new HashSet<string>(All, StringComparer.OrdinalIgnoreCase),
        ["supervisor"] = new HashSet<string>(All, StringComparer.OrdinalIgnoreCase),
        ["recruiter"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            View, ViewPeople, ViewAnalytics, Export
        },
        ["sales"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Sales cannot access Core HR organization APIs.
        }
    };

    public static bool Has(IEnumerable<string> roleCodes, string capability)
    {
        var list = roleCodes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList();
        if (list.Count == 0) return true;

        foreach (var code in list)
        {
            if (RoleCapabilities.TryGetValue(code, out var caps) && caps.Contains(capability))
                return true;
        }
        return false;
    }

    public static IReadOnlyList<string> ListFor(IEnumerable<string> roleCodes)
    {
        var codes = roleCodes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList();
        if (codes.Count == 0) return All.ToList();

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in codes)
        {
            if (RoleCapabilities.TryGetValue(code, out var caps))
                foreach (var c in caps) set.Add(c);
        }
        return set.ToList();
    }
}
