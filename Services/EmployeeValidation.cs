using System.Net.Mail;
using nexthire_api.DTOs.CoreHr;

namespace nexthire_api.Services;

/// <summary>Pure (database-free) Core HR validation rules, shared by the service and unit tests.</summary>
public static class EmployeeValidation
{
    public static readonly HashSet<string> WorkModalities = new(StringComparer.OrdinalIgnoreCase)
    {
        "OnSite", "Remote", "Hybrid"
    };

    public static readonly HashSet<string> PayFrequencies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Hourly", "Weekly", "BiWeekly", "SemiMonthly", "Monthly", "Annual"
    };

    public static readonly HashSet<string> DocumentStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Active", "Expired", "Archived", "Pending"
    };

    public static bool IsValidStatus(string? status) =>
        !string.IsNullOrWhiteSpace(status) && EmploymentStatuses.All.Contains(status.Trim());

    /// <summary>Returns the canonical casing of a status (e.g. "onleave" -> "OnLeave"), or null if invalid.</summary>
    public static string? NormalizeStatus(string? status)
    {
        if (!IsValidStatus(status)) return null;
        return EmploymentStatuses.All.First(s => s.Equals(status!.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static string? NormalizeFromSet(HashSet<string> set, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return set.FirstOrDefault(s => s.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Walks the manager chain starting at <paramref name="proposedManagerId"/>.
    /// Returns true when <paramref name="employeeId"/> appears in the chain (or is the proposed manager), i.e. a cycle.
    /// </summary>
    public static bool WouldCreateManagerCycle(Guid employeeId, Guid proposedManagerId, Func<Guid, Guid?> managerOf, int maxDepth = 1000)
    {
        if (proposedManagerId == employeeId) return true;

        var visited = new HashSet<Guid>();
        Guid? current = proposedManagerId;
        var depth = 0;
        while (current.HasValue && depth++ < maxDepth)
        {
            if (current.Value == employeeId) return true;
            if (!visited.Add(current.Value)) return false;
            current = managerOf(current.Value);
        }
        return false;
    }

    /// <summary>A candidate application counts as hired by pipeline stage name or application status (case-insensitive).</summary>
    public static bool IsHired(string? stageName, string? status) =>
        string.Equals(stageName?.Trim(), "hired", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status?.Trim(), "hired", StringComparison.OrdinalIgnoreCase);

    public static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        if (email.Length > 256) return false;
        try
        {
            var addr = new MailAddress(email.Trim());
            return addr.Address.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Returns a list of human-readable validation errors for a create/update payload (empty when valid).</summary>
    public static List<string> ValidateEmployee(CreateEmployeeRequest r)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(r.FirstName)) errors.Add("First name is required.");
        else if (r.FirstName.Trim().Length > 100) errors.Add("First name must be 100 characters or fewer.");

        if (string.IsNullOrWhiteSpace(r.LastName)) errors.Add("Last name is required.");
        else if (r.LastName.Trim().Length > 100) errors.Add("Last name must be 100 characters or fewer.");

        if (!string.IsNullOrWhiteSpace(r.EmploymentStatus) && !IsValidStatus(r.EmploymentStatus))
            errors.Add($"Employment status must be one of: {string.Join(", ", EmploymentStatuses.All.OrderBy(s => s))}.");

        if (!string.IsNullOrWhiteSpace(r.WorkModality) && NormalizeFromSet(WorkModalities, r.WorkModality) == null)
            errors.Add("Work modality must be OnSite, Remote or Hybrid.");

        if (!string.IsNullOrWhiteSpace(r.WorkEmail) && !IsValidEmail(r.WorkEmail))
            errors.Add("Work email is not a valid email address.");
        if (!string.IsNullOrWhiteSpace(r.PersonalEmail) && !IsValidEmail(r.PersonalEmail))
            errors.Add("Personal email is not a valid email address.");

        if (!string.IsNullOrWhiteSpace(r.EmployeeNumber) && r.EmployeeNumber.Trim().Length > 50)
            errors.Add("Employee number must be 50 characters or fewer.");

        if (r.DateOfBirth.HasValue && r.DateOfBirth.Value.Date > DateTime.UtcNow.Date)
            errors.Add("Date of birth cannot be in the future.");

        if (r.ContractStartDate.HasValue && r.ContractEndDate.HasValue && r.ContractEndDate.Value.Date < r.ContractStartDate.Value.Date)
            errors.Add("Contract end date cannot be before the contract start date.");

        if (r.EmergencyContacts != null)
        {
            if (r.EmergencyContacts.Any(c => string.IsNullOrWhiteSpace(c.FullName)))
                errors.Add("Every emergency contact needs a full name.");
        }

        return errors;
    }
}
