using nexthire_api.DTOs.CoreHr;

namespace nexthire_api.Services;

/// <summary>Pure (database-free) rules for the Core HR Documents hub, shared by the service, repository and unit tests.</summary>
public static class HrDocumentValidation
{
    public const int MaxNameLength = 300;
    public const int MaxRequestTitleLength = 300;
    public const int MaxRequestNotesLength = 1000;
    public const long MaxUploadBytes = 10L * 1024 * 1024;

    /// <summary>Statuses a caller may set directly (Archived is only reachable via DELETE).</summary>
    public static readonly HashSet<string> WritableStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Active", "Pending", "Expired"
    };

    private static readonly Dictionary<string, string> CategoryAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Contracts"] = HrDocumentCategories.Contract,
        ["ID"] = HrDocumentCategories.Identification,
        ["Identity"] = HrDocumentCategories.Identification,
        ["Certificate"] = HrDocumentCategories.Certification,
        ["Certificates"] = HrDocumentCategories.Certification,
        ["Certifications"] = HrDocumentCategories.Certification,
        ["Admin"] = HrDocumentCategories.Administrative,
        ["Taxes"] = HrDocumentCategories.Tax,
        ["Policies"] = HrDocumentCategories.Policy
    };

    /// <summary>Canonical category for a known value or alias (case-insensitive); null when blank or unknown.</summary>
    public static string? NormalizeCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return null;
        var value = category.Trim();
        var canonical = HrDocumentCategories.All.FirstOrDefault(c => c.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (canonical != null) return canonical;
        return CategoryAliases.TryGetValue(value, out var alias) ? alias : null;
    }

    /// <summary>Every stored spelling (canonical name + aliases) that rolls up into <paramref name="canonical"/>.</summary>
    public static IReadOnlyList<string> StoredValuesFor(string canonical)
    {
        var values = new List<string> { canonical };
        values.AddRange(CategoryAliases.Where(kv => kv.Value.Equals(canonical, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key));
        return values;
    }

    /// <summary>Every stored spelling of the non-"Other" categories; anything outside this set displays as Other.</summary>
    public static IReadOnlyList<string> KnownNonOtherStoredValues() =>
        HrDocumentCategories.All
            .Where(c => c != HrDocumentCategories.Other)
            .SelectMany(StoredValuesFor)
            .ToList();

    /// <summary>Category for display: legacy / free-form stored values that are not canonical roll up into "Other".</summary>
    public static string NormalizeCategoryForDisplay(string? category) =>
        NormalizeCategory(category) ?? HrDocumentCategories.Other;

    /// <summary>Canonical display status (Active, Expiring, Expired, Pending) or null when invalid.</summary>
    public static string? NormalizeDisplayStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;
        var value = status.Trim();
        // "Valid"/"Completed" are accepted synonyms used by the hub UI for Active.
        if (value.Equals("Valid", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Completed", StringComparison.OrdinalIgnoreCase))
            return HrDocumentDisplayStatuses.Active;
        return HrDocumentDisplayStatuses.All.FirstOrDefault(s => s.Equals(value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Computes the UI status. Archived documents are not displayed (callers skip them) and return "Archived".
    /// Pending stays Pending; a past expiry (or stored Expired) is Expired; an expiry within the window is Expiring.
    /// </summary>
    public static string ComputeDisplayStatus(
        string? storedStatus,
        DateTime? expiresAt,
        DateTime today,
        int windowDays = HrDocumentDisplayStatuses.ExpiringWindowDays)
    {
        if (string.Equals(storedStatus, "Archived", StringComparison.OrdinalIgnoreCase)) return "Archived";
        if (string.Equals(storedStatus, "Pending", StringComparison.OrdinalIgnoreCase)) return HrDocumentDisplayStatuses.Pending;
        if (expiresAt.HasValue && expiresAt.Value.Date < today.Date) return HrDocumentDisplayStatuses.Expired;
        if (string.Equals(storedStatus, "Expired", StringComparison.OrdinalIgnoreCase)) return HrDocumentDisplayStatuses.Expired;
        if (expiresAt.HasValue && expiresAt.Value.Date <= today.Date.AddDays(windowDays)) return HrDocumentDisplayStatuses.Expiring;
        return HrDocumentDisplayStatuses.Active;
    }

    /// <summary>An Open request whose due date has passed is reported as Overdue; other statuses are unchanged.</summary>
    public static string ComputeRequestStatus(string storedStatus, DateTime? dueAt, DateTime today) =>
        string.Equals(storedStatus, HrDocumentRequestStatuses.Open, StringComparison.OrdinalIgnoreCase)
        && dueAt.HasValue && dueAt.Value.Date < today.Date
            ? HrDocumentRequestStatuses.Overdue
            : storedStatus;

    public static bool IsHttpUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Metadata shared by create / upload / update. Returns human-readable errors (empty when valid).</summary>
    public static List<string> ValidateMetadata(string? category, string? name, string? status, string? externalUrl)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(category)) errors.Add("Category is required.");
        else if (NormalizeCategory(category) == null)
            errors.Add($"Category must be one of: {string.Join(", ", HrDocumentCategories.All)}.");

        if (string.IsNullOrWhiteSpace(name)) errors.Add("Name is required.");
        else if (name.Trim().Length > MaxNameLength) errors.Add($"Name must be {MaxNameLength} characters or fewer.");

        if (!string.IsNullOrWhiteSpace(status) && !WritableStatuses.Contains(status.Trim()))
            errors.Add("Status must be Active, Pending or Expired.");

        if (!string.IsNullOrWhiteSpace(externalUrl))
        {
            if (!IsHttpUrl(externalUrl)) errors.Add("External URL must be an http(s) URL.");
            else if (externalUrl.Trim().Length > 1000) errors.Add("External URL must be 1000 characters or fewer.");
        }

        return errors;
    }

    public static List<string> ValidateCreate(CreateHrDocumentRequest request)
    {
        var errors = new List<string>();
        if (request.EmployeeId == Guid.Empty) errors.Add("employeeId is required.");
        errors.AddRange(ValidateMetadata(request.Category, request.Name, request.Status, request.ExternalUrl));
        if (request.DocumentId == null && string.IsNullOrWhiteSpace(request.ExternalUrl))
            errors.Add("Provide a documentId or an externalUrl.");
        return errors;
    }

    public static List<string> ValidateDocumentRequest(CreateDocumentRequest request)
    {
        var errors = new List<string>();
        if (request.EmployeeId == Guid.Empty) errors.Add("employeeId is required.");

        if (string.IsNullOrWhiteSpace(request.Category)) errors.Add("Category is required.");
        else if (NormalizeCategory(request.Category) == null)
            errors.Add($"Category must be one of: {string.Join(", ", HrDocumentCategories.All)}.");

        if (string.IsNullOrWhiteSpace(request.Title)) errors.Add("Title is required.");
        else if (request.Title.Trim().Length > MaxRequestTitleLength)
            errors.Add($"Title must be {MaxRequestTitleLength} characters or fewer.");

        if (!string.IsNullOrWhiteSpace(request.Notes) && request.Notes.Trim().Length > MaxRequestNotesLength)
            errors.Add($"Notes must be {MaxRequestNotesLength} characters or fewer.");

        return errors;
    }

    /// <summary>Default document name for uploads: the file name without its extension.</summary>
    public static string DefaultNameFromFileName(string? fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName ?? string.Empty).Trim();
        return name.Length == 0 ? "Document" : name;
    }
}
