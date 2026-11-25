namespace nexthire_api.Security;

/// <summary>
/// Core HR capability strings mapped onto existing NextHire role codes
/// (account_admin, supervisor, recruiter, sales) — no parallel ACL tables.
/// </summary>
public static class HrPermissions
{
    public const string View = "Employees.View";
    public const string Create = "Employees.Create";
    public const string Edit = "Employees.Edit";
    public const string Archive = "Employees.Archive";
    public const string Import = "Employees.Import";
    public const string Export = "Employees.Export";
    public const string ViewSensitiveData = "Employees.ViewSensitiveData";
    public const string ManageDocuments = "Employees.ManageDocuments";
    public const string ViewCompensation = "Employees.ViewCompensation";
    public const string ManageCompensation = "Employees.ManageCompensation";

    private static readonly Dictionary<string, HashSet<string>> RoleCapabilities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["account_admin"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            View, Create, Edit, Archive, Import, Export,
            ViewSensitiveData, ManageDocuments, ViewCompensation, ManageCompensation
        },
        ["supervisor"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            View, Create, Edit, Archive, Import, Export,
            ViewSensitiveData, ManageDocuments, ViewCompensation, ManageCompensation
        },
        ["recruiter"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            View, Create, Edit, Export, ManageDocuments
        },
        ["sales"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Sales cannot access Core HR employees APIs (middleware + empty set).
        }
    };

    public static bool Has(IEnumerable<string> roleCodes, string capability)
    {
        foreach (var code in roleCodes)
        {
            if (string.IsNullOrWhiteSpace(code)) continue;
            if (RoleCapabilities.TryGetValue(code.Trim(), out var caps) && caps.Contains(capability))
                return true;
        }
        // No roles assigned: treat as full access (matches existing ATS behavior for unscoped users).
        var list = roleCodes.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        return list.Count == 0;
    }

    public static IReadOnlyList<string> ListFor(IEnumerable<string> roleCodes)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var codes = roleCodes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList();
        if (codes.Count == 0)
        {
            foreach (var caps in RoleCapabilities.Values)
                foreach (var c in caps) set.Add(c);
            return set.ToList();
        }

        foreach (var code in codes)
        {
            if (RoleCapabilities.TryGetValue(code, out var caps))
                foreach (var c in caps) set.Add(c);
        }
        return set.ToList();
    }
}
