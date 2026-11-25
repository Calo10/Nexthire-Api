namespace nexthire_api.Security;

/// <summary>
/// Core HR Reports and People Analytics capabilities mapped onto existing role codes.
/// No parallel ACL tables. Recruitment datasets are never exposed here.
/// </summary>
public static class ReportsPermissions
{
    public const string View = "Reports.View";
    public const string ViewWorkforce = "Reports.ViewWorkforce";
    public const string ViewRetention = "Reports.ViewRetention";
    public const string ViewOnboarding = "Reports.ViewOnboarding";
    public const string ViewOffboarding = "Reports.ViewOffboarding";
    public const string ViewOrganization = "Reports.ViewOrganization";
    public const string ViewCompliance = "Reports.ViewCompliance";
    public const string ViewCompensation = "Reports.ViewCompensation";
    public const string Create = "Reports.Create";
    public const string Edit = "Reports.Edit";
    public const string Share = "Reports.Share";
    public const string Export = "Reports.Export";
    public const string Schedule = "Reports.Schedule";
    public const string ManageDashboards = "Reports.ManageDashboards";
    public const string ManageOrganizationReports = "Reports.ManageOrganizationReports";
    public const string ViewExecutionHistory = "Reports.ViewExecutionHistory";

    public static readonly IReadOnlyList<string> All =
    [
        View, ViewWorkforce, ViewRetention, ViewOnboarding, ViewOffboarding,
        ViewOrganization, ViewCompliance, ViewCompensation,
        Create, Edit, Share, Export, Schedule, ManageDashboards,
        ManageOrganizationReports, ViewExecutionHistory
    ];

    private static readonly Dictionary<string, HashSet<string>> RoleCapabilities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["account_admin"] = new HashSet<string>(All, StringComparer.OrdinalIgnoreCase),
        ["supervisor"] = new HashSet<string>(All, StringComparer.OrdinalIgnoreCase),
        ["recruiter"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Recruiter may see operational HR reports but not compensation aggregates.
            View, ViewWorkforce, ViewOnboarding, ViewOffboarding, ViewOrganization,
            ViewCompliance, Export, Create, Edit
        },
        ["sales"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Sales cannot access Core HR reports.
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
