namespace nexthire_api.Security;

/// <summary>
/// Core HR offboarding capability strings mapped onto existing NextHire role codes
/// (account_admin, supervisor, recruiter, sales) — no parallel ACL tables.
/// Mirrors <see cref="OnboardingPermissions"/>.
/// </summary>
public static class OffboardingPermissions
{
    public const string View = "Offboarding.View";
    public const string Create = "Offboarding.Create";
    public const string Edit = "Offboarding.Edit";
    public const string Cancel = "Offboarding.Cancel";
    public const string Complete = "Offboarding.Complete";
    public const string ManageTemplates = "Offboarding.ManageTemplates";
    public const string ManageTasks = "Offboarding.ManageTasks";
    /// <summary>Approve / reject / waive tasks (clearance sign-off).</summary>
    public const string ApproveClearance = "Offboarding.ApproveClearance";
    public const string ManageAssets = "Offboarding.ManageAssets";
    public const string ManageAccess = "Offboarding.ManageAccess";
    public const string ManageDocuments = "Offboarding.ManageDocuments";
    public const string ManageExitInterviews = "Offboarding.ManageExitInterviews";
    /// <summary>See separation reason, confidential notes and exit interview responses / HR notes.</summary>
    public const string ViewConfidentialInformation = "Offboarding.ViewConfidentialInformation";
    public const string ManageSettlement = "Offboarding.ManageSettlement";
    public const string ManageAutomations = "Offboarding.ManageAutomations";

    public static readonly IReadOnlyList<string> All =
    [
        View, Create, Edit, Cancel, Complete, ManageTemplates, ManageTasks, ApproveClearance,
        ManageAssets, ManageAccess, ManageDocuments, ManageExitInterviews,
        ViewConfidentialInformation, ManageSettlement, ManageAutomations
    ];

    private static readonly Dictionary<string, HashSet<string>> RoleCapabilities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["account_admin"] = new HashSet<string>(All, StringComparer.OrdinalIgnoreCase),
        ["supervisor"] = new HashSet<string>(All, StringComparer.OrdinalIgnoreCase),
        ["recruiter"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Deliberately no ViewConfidentialInformation: separation reasons and exit interviews stay with HR leadership.
            View, Create, Edit, ManageTasks
        },
        ["sales"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Sales cannot access Core HR offboarding APIs (middleware allowlist + empty set).
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
