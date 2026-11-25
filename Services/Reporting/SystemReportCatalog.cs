using System.Security.Cryptography;
using System.Text;
using nexthire_api.Repositories;

namespace nexthire_api.Services.Reporting;

/// <summary>
/// Built-in Core HR report cards (is_system = 1). Each card is a dataset + configuration from <see cref="ReportDatasetRegistry"/>;
/// ids are deterministic per organization so seeding is idempotent.
/// </summary>
public static class SystemReportCatalog
{
    public const string CategoryWorkforce = "Workforce";
    public const string CategoryRetention = "Retention";
    public const string CategoryOnboarding = "Onboarding";
    public const string CategoryOffboarding = "Offboarding";
    public const string CategoryCompliance = "Compliance";
    public const string CategoryOrganization = "Organization";
    public const string CategoryCompensation = "Compensation";

    public static readonly IReadOnlyList<string> Categories =
    [
        CategoryWorkforce, CategoryRetention, CategoryOnboarding, CategoryOffboarding,
        CategoryCompliance, CategoryOrganization, CategoryCompensation, "Custom"
    ];

    /// <summary>Stable id for a system report of an organization.</summary>
    public static Guid IdFor(Guid organizationId, string key)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes($"nh-hr-system-report:{organizationId:N}:{key}"));
        return new Guid(bytes);
    }

    private static string Detail(string columns, string? filters = null, string? sort = null, int limit = 2000) =>
        $"{{\"columns\":[{columns}],\"measures\":[],\"filters\":[{filters}],\"sort\":[{sort}],\"chartType\":\"table\",\"limit\":{limit}}}";

    private static string Agg(string dimensions, string measures, string chart, string? filters = null, string? sort = null, int limit = 500) =>
        $"{{\"dimensions\":[{dimensions}],\"measures\":[{measures}],\"filters\":[{filters}],\"sort\":[{sort}],\"chartType\":\"{chart}\",\"limit\":{limit}}}";

    private const string CountId = "{\"field\":\"id\",\"agg\":\"count\"}";
    private const string NotTerminated = "{\"field\":\"employment_status\",\"op\":\"neq\",\"value\":\"Terminated\"}";

    public static readonly IReadOnlyList<SystemReportSeed> All =
    [
        new("employee_directory", "Employee Directory", "Searchable list of employees with department, manager, location and hire date.",
            CategoryWorkforce, "employees",
            Detail("\"employee_number\",\"name\",\"job_title\",\"department\",\"manager\",\"location\",\"employment_type\",\"employment_status\",\"hire_date\"",
                NotTerminated, "{\"field\":\"name\",\"dir\":\"asc\"}"),
            null),

        new("headcount_by_department", "Headcount by Department", "Current employees per department.",
            CategoryWorkforce, "employees", Agg("\"department\"", CountId, "bar", NotTerminated), null),

        new("headcount_by_location", "Headcount by Location", "Current employees per work location.",
            CategoryWorkforce, "employees", Agg("\"location\"", CountId, "bar", NotTerminated), null),

        new("workforce_by_employment_type", "Workforce by Employment Type", "Current employees by employment type.",
            CategoryWorkforce, "employees", Agg("\"employment_type\"", CountId, "donut", NotTerminated), null),

        new("new_hires_by_month", "New Hires by Month", "Hires per month.",
            CategoryWorkforce, "employees",
            Agg("\"hire_month\"", CountId, "line", "{\"field\":\"hire_date\",\"op\":\"isNotNull\"}", "{\"field\":\"hire_month\",\"dir\":\"asc\"}"), null),

        new("tenure_distribution", "Tenure Distribution", "Current employees grouped by length of service.",
            CategoryWorkforce, "employees", Agg("\"tenure_bucket\"", CountId, "column", NotTerminated, "{\"field\":\"tenure_bucket\",\"dir\":\"asc\"}"), null),

        new("turnover_analysis", "Turnover Analysis", "Terminations per month and department.",
            CategoryRetention, "employees",
            Agg("\"termination_month\",\"department\"", CountId, "stackedBar",
                "{\"field\":\"employment_status\",\"op\":\"eq\",\"value\":\"Terminated\"},{\"field\":\"termination_date\",\"op\":\"isNotNull\"}",
                "{\"field\":\"termination_month\",\"dir\":\"asc\"}"), null),

        new("separations_by_type", "Separations by Type", "Offboardings grouped by separation type.",
            CategoryRetention, "offboardings",
            Agg("\"separation_type\"", CountId, "donut", "{\"field\":\"status\",\"op\":\"neq\",\"value\":\"Cancelled\"}"), null),

        new("onboarding_completion", "Onboarding Completion", "Onboardings by status.",
            CategoryOnboarding, "onboardings", Agg("\"status\"", CountId, "donut"), null),

        new("onboarding_progress_by_department", "Onboarding Progress by Department", "Average onboarding progress per department.",
            CategoryOnboarding, "onboardings",
            Agg("\"department\"", "{\"field\":\"progress_pct\",\"agg\":\"avg\"},{\"field\":\"id\",\"agg\":\"count\"}", "bar",
                "{\"field\":\"status\",\"op\":\"in\",\"value\":[\"NotStarted\",\"InProgress\",\"InReview\"]}"), null),

        new("overdue_onboarding_tasks", "Overdue Onboarding Tasks", "Open onboarding tasks past their due date.",
            CategoryOnboarding, "onboarding_tasks",
            Detail("\"title\",\"category\",\"status\",\"employee\",\"department\",\"due_date\"",
                "{\"field\":\"is_overdue\",\"op\":\"eq\",\"value\":1}", "{\"field\":\"due_date\",\"dir\":\"asc\"}"), null),

        new("offboarding_clearance", "Offboarding Clearance", "Offboardings by status with pending assets and access revocations.",
            CategoryOffboarding, "offboardings",
            Agg("\"status\"",
                "{\"field\":\"id\",\"agg\":\"count\"},{\"field\":\"pending_assets\",\"agg\":\"sum\"},{\"field\":\"pending_access\",\"agg\":\"sum\"}", "bar",
                "{\"field\":\"status\",\"op\":\"neq\",\"value\":\"Cancelled\"}"), null),

        new("document_compliance", "Document Compliance", "Employee documents by compliance status.",
            CategoryCompliance, "employee_documents", Agg("\"display_status\"", CountId, "donut"), null),

        new("expiring_documents", "Expiring Documents", "Documents that are expired or expire within 30 days.",
            CategoryCompliance, "employee_documents",
            Detail("\"name\",\"category\",\"display_status\",\"employee\",\"department\",\"expires_at\"",
                "{\"field\":\"display_status\",\"op\":\"in\",\"value\":[\"Expired\",\"Expiring\"]}", "{\"field\":\"expires_at\",\"dir\":\"asc\"}"), null),

        new("open_document_requests", "Open Document Requests", "Requests still waiting on employees.",
            CategoryCompliance, "document_requests",
            Detail("\"title\",\"category\",\"status\",\"employee\",\"department\",\"due_at\",\"days_open\"",
                "{\"field\":\"status\",\"op\":\"in\",\"value\":[\"Open\",\"Overdue\"]}", "{\"field\":\"due_at\",\"dir\":\"asc\"}"), null),

        new("position_vacancies", "Position Vacancies", "Approved headcount, filled seats and vacancies per department.",
            CategoryOrganization, "organizational_positions",
            Agg("\"department\"",
                "{\"field\":\"approved_headcount\",\"agg\":\"sum\"},{\"field\":\"filled_count\",\"agg\":\"sum\"},{\"field\":\"vacancies\",\"agg\":\"sum\"}", "stackedBar"), null),

        new("location_distribution", "Location Distribution", "Work locations and their employee counts.",
            CategoryOrganization, "work_locations",
            Detail("\"name\",\"city\",\"country\",\"location_type\",\"is_headquarters\",\"employee_count\"", null, "{\"field\":\"employee_count\",\"dir\":\"desc\"}"), null),

        new("compensation_by_department", "Compensation by Department", "Average annualized compensation per department (groups of 5+ employees).",
            CategoryCompensation, "compensation",
            Agg("\"department\",\"currency\"", "{\"field\":\"annualized_amount\",\"agg\":\"avg\"},{\"field\":\"id\",\"agg\":\"count\"}", "bar"), null)
    ];
}
