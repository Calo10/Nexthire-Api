namespace nexthire_api.Security;

/// <summary>
/// Core HR onboarding capability strings mapped onto existing NextHire role codes
/// (account_admin, supervisor, recruiter, sales) — no parallel ACL tables.
/// Mirrors <see cref="HrPermissions"/>.
/// </summary>
public static class OnboardingPermissions
{
    public const string View = "Onboarding.View";
    public const string Create = "Onboarding.Create";
    public const string Edit = "Onboarding.Edit";
    public const string Cancel = "Onboarding.Cancel";
    public const string Complete = "Onboarding.Complete";
    public const string ManageTemplates = "Onboarding.ManageTemplates";
    public const string ManageTasks = "Onboarding.ManageTasks";
    public const string ApproveTasks = "Onboarding.ApproveTasks";
    public const string ManageDocuments = "Onboarding.ManageDocuments";
    public const string ManageAutomations = "Onboarding.ManageAutomations";

    public static readonly IReadOnlyList<string> All =
    [
        View, Create, Edit, Cancel, Complete, ManageTemplates,
        ManageTasks, ApproveTasks, ManageDocuments, ManageAutomations
    ];

    private static readonly Dictionary<string, HashSet<string>> RoleCapabilities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["account_admin"] = new HashSet<string>(All, StringComparer.OrdinalIgnoreCase),
        ["supervisor"] = new HashSet<string>(All, StringComparer.OrdinalIgnoreCase),
        ["recruiter"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            View, Create, Edit, ManageTasks
        },
        ["sales"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Sales cannot access Core HR onboarding APIs (middleware allowlist + empty set).
        }
    };

    public static bool Has(IEnumerable<string> roleCodes, string capability)
    {
        var list = roleCodes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList();

        // No roles assigned: treat as full access (matches existing ATS behavior for unscoped users).
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
