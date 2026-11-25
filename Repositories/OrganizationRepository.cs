using System.Data;
using System.Data.SqlClient;
using System.Text;
using Dapper;
using nexthire_api.Data;
using nexthire_api.DTOs;
using nexthire_api.DTOs.CoreHr;
using nexthire_api.Exceptions;
using nexthire_api.Services;

namespace nexthire_api.Repositories;

/// <summary>
/// Dapper/SQL Server implementation of Core HR Organization (departments hierarchy, positions, levels, locations,
/// reporting lines and organizational activity). Every query is tenant scoped by organization_id and archives are soft.
/// </summary>
public class OrganizationRepository : IOrganizationRepository
{
    private const string SchemaScriptFileName = "013-core-hr-organization-schema.sql";
    private const int ChartMemberCount = 4;
    private const int RecentWindowDays = 90;

    private const string EmployeeNameSql =
        "COALESCE(NULLIF(LTRIM(RTRIM(e.preferred_name)), ''), LTRIM(RTRIM(e.first_name + ' ' + e.last_name)))";

    private static string NameSql(string alias) =>
        $"COALESCE(NULLIF(LTRIM(RTRIM({alias}.preferred_name)), ''), LTRIM(RTRIM({alias}.first_name + ' ' + {alias}.last_name)))";

    /// <summary>Employee alias <c>e</c> that is a current (non-archived, not terminated) member of the workforce.</summary>
    private const string ActiveEmployeeSql = "e.archived_at IS NULL AND e.employment_status <> N'Terminated'";

    /// <summary>Correlated count of active seats filled for position alias <c>p</c>.</summary>
    private const string FilledCountSql =
        @"(SELECT COUNT(1) FROM dbo.nh_hr_employee_position_assignments a
           WHERE a.organization_id = p.organization_id AND a.position_id = p.id
             AND a.end_date IS NULL AND a.status = N'Active')";

    private const string ActivePositionSql = "p.archived_at IS NULL AND p.status <> N'Archived'";

    private static readonly string DepartmentSelectSql = $@"
SELECT d.id AS Id, d.name AS Name, d.code AS Code, d.description AS Description,
       d.parent_department_id AS ParentDepartmentId, pd.name AS ParentDepartmentName,
       d.head_employee_id AS HeadEmployeeId, {NameSql("h")} AS HeadName,
       h.profile_picture_url AS HeadPhotoUrl, hjt.name AS HeadJobTitle,
       d.cost_center AS CostCenter, d.color AS Color, ISNULL(d.icon, N'default') AS Icon,
       d.display_order AS DisplayOrder, d.is_active AS IsActive,
       d.created_at AS CreatedAt, d.updated_at AS UpdatedAt,
       (SELECT COUNT(1) FROM dbo.nh_hr_employees e
         WHERE e.organization_id = d.organization_id AND e.department_id = d.id AND {ActiveEmployeeSql}) AS DirectEmployeeCount,
       ISNULL(pa.PositionCount, 0) AS PositionCount,
       ISNULL(pa.OpenCount, 0) AS OpenPositionCount,
       pa.Budget AS BudgetedHeadcount,
       ISNULL(pa.Filled, 0) AS FilledHeadcount
FROM dbo.nh_hr_departments d
LEFT JOIN dbo.nh_hr_departments pd ON pd.id = d.parent_department_id AND pd.organization_id = d.organization_id
LEFT JOIN dbo.nh_hr_employees h ON h.id = d.head_employee_id AND h.organization_id = d.organization_id
LEFT JOIN dbo.nh_hr_job_titles hjt ON hjt.id = h.job_title_id AND hjt.organization_id = h.organization_id
OUTER APPLY (
    SELECT COUNT(1) AS PositionCount,
           SUM(CASE WHEN fc.n < p.approved_headcount THEN 1 ELSE 0 END) AS OpenCount,
           SUM(p.approved_headcount) AS Budget,
           SUM(CASE WHEN fc.n < p.approved_headcount THEN fc.n ELSE p.approved_headcount END) AS Filled
    FROM dbo.nh_hr_organizational_positions p
    CROSS APPLY (SELECT COUNT(1) AS n FROM dbo.nh_hr_employee_position_assignments a
                  WHERE a.organization_id = p.organization_id AND a.position_id = p.id
                    AND a.end_date IS NULL AND a.status = N'Active') fc
    WHERE p.organization_id = d.organization_id AND p.department_id = d.id AND {ActivePositionSql}
) pa
WHERE d.organization_id = @OrganizationId
  AND (@IncludeArchived = 1 OR (d.archived_at IS NULL AND d.is_active = 1))
ORDER BY d.display_order, d.name;";

    private static readonly string PositionSelectSql = $@"
SELECT p.id AS Id, p.position_name AS Title, p.position_code AS Code,
       p.job_title_id AS JobTitleId, jt.name AS JobTitle,
       p.department_id AS DepartmentId, d.name AS DepartmentName,
       p.reports_to_position_id AS ReportsToPositionId, rp.position_name AS ReportsToPositionName,
       p.work_location_id AS WorkLocationId, wl.name AS LocationName,
       p.organizational_level_id AS OrganizationalLevelId, p.organizational_level_id AS LevelId, ol.name AS LevelName,
       p.status AS Status, p.approved_headcount AS Headcount,
       {FilledCountSql} AS FilledCount,
       CAST(CASE WHEN p.archived_at IS NULL AND p.status <> N'Archived' THEN 1 ELSE 0 END AS bit) AS IsActive,
       p.effective_date AS EffectiveDate";

    private const string PositionFromSql = @"
FROM dbo.nh_hr_organizational_positions p
JOIN dbo.nh_hr_departments d ON d.id = p.department_id AND d.organization_id = p.organization_id
LEFT JOIN dbo.nh_hr_job_titles jt ON jt.id = p.job_title_id AND jt.organization_id = p.organization_id
LEFT JOIN dbo.nh_hr_organizational_positions rp ON rp.id = p.reports_to_position_id AND rp.organization_id = p.organization_id
LEFT JOIN dbo.nh_hr_work_locations wl ON wl.id = p.work_location_id AND wl.organization_id = p.organization_id
LEFT JOIN dbo.nh_hr_organizational_levels ol ON ol.id = p.organizational_level_id AND ol.organization_id = p.organization_id";

    private const string LocationSelectSql = @"
SELECT l.id AS Id, l.name AS Name, l.city AS City, l.state AS State, l.country AS Country,
       l.address_line1 AS AddressLine, l.timezone AS Timezone,
       ISNULL(l.location_type, N'Office') AS LocationType,
       CAST(CASE WHEN l.location_type = N'Remote' THEN 1 ELSE 0 END AS bit) AS IsRemote,
       l.is_headquarters AS IsHeadquarters, l.is_active AS IsActive,
       (SELECT COUNT(1) FROM dbo.nh_hr_employees e
         WHERE e.organization_id = l.organization_id AND e.work_location_id = l.id AND e.archived_at IS NULL
           AND e.employment_status <> N'Terminated') AS EmployeeCount,
       (SELECT COUNT(DISTINCT e.department_id) FROM dbo.nh_hr_employees e
         WHERE e.organization_id = l.organization_id AND e.work_location_id = l.id AND e.archived_at IS NULL
           AND e.employment_status <> N'Terminated' AND e.department_id IS NOT NULL) AS DepartmentCount
FROM dbo.nh_hr_work_locations l";

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<OrganizationRepository> _logger;

    public OrganizationRepository(IDbConnectionFactory connectionFactory, ILogger<OrganizationRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // Schema / levels / demo data
    // -------------------------------------------------------------------------

    public async Task EnsureSchemaAsync()
    {
        var path = ResolveSchemaScriptPath();
        var sql = await File.ReadAllTextAsync(path);
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        await connection.ExecuteAsync(new CommandDefinition(sql));
    }

    public async Task EnsureOrgLevelsAsync(Guid organizationId, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        await EnsureLevelsCoreAsync(connection, null, organizationId, actorUserId, cancellationToken);
    }

    private static async Task EnsureLevelsCoreAsync(IDbConnection connection, IDbTransaction? tx, Guid organizationId, Guid? actorUserId, CancellationToken ct)
    {
        var count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM dbo.nh_hr_organizational_levels WHERE organization_id = @OrganizationId;",
            new { OrganizationId = organizationId }, tx, cancellationToken: ct));
        if (count > 0) return;

        foreach (var (name, order, description) in OrganizationDefaultLevels.Defaults)
            await UpsertLevelCoreAsync(connection, tx, organizationId, name, description, order, actorUserId, ct);
    }

    private static async Task<Guid> UpsertLevelCoreAsync(
        IDbConnection connection, IDbTransaction? tx, Guid organizationId, string name, string? description, int levelOrder, Guid? actorUserId, CancellationToken ct)
    {
        var trimmed = name.Trim();
        var now = DateTimeOffset.UtcNow;
        var existing = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM dbo.nh_hr_organizational_levels WHERE organization_id = @OrganizationId AND name = @Name;",
            new { OrganizationId = organizationId, Name = trimmed }, tx, cancellationToken: ct));

        if (existing.HasValue)
        {
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_organizational_levels
SET description = COALESCE(@Description, description), level_order = @LevelOrder, is_active = 1,
    updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND id = @Id;",
                new { OrganizationId = organizationId, Id = existing.Value, Description = Trim(description), LevelOrder = levelOrder, Now = now, ActorUserId = actorUserId },
                tx, cancellationToken: ct));
            return existing.Value;
        }

        var id = Guid.NewGuid();
        await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_organizational_levels (id, organization_id, name, description, level_order, is_active, created_at, created_by)
VALUES (@Id, @OrganizationId, @Name, @Description, @LevelOrder, 1, @Now, @ActorUserId);",
            new { Id = id, OrganizationId = organizationId, Name = trimmed, Description = Trim(description), LevelOrder = levelOrder, Now = now, ActorUserId = actorUserId },
            tx, cancellationToken: ct));
        return id;
    }

    private sealed record DemoDeptStyle(string Icon, string Color, string Description, int Order);

    private static readonly Dictionary<string, DemoDeptStyle> DemoStyles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Operations"] = new("operations", "#6366F1", "Runs day-to-day delivery, logistics and service operations.", 10),
        ["Sales"] = new("sales", "#10B981", "Revenue generation, accounts and business development.", 20),
        ["IT"] = new("technology", "#0EA5E9", "Internal systems, infrastructure and technical support.", 30),
        ["Administration"] = new("administration", "#64748B", "Office administration, HR operations and compliance.", 40),
        ["Hospitality"] = new("hospitality", "#F59E0B", "Guest experience, front desk and concierge services.", 50),
        ["Technology"] = new("technology", "#0EA5E9", "Product engineering and technology strategy.", 60),
        ["People & Culture"] = new("people", "#EC4899", "Talent, culture, learning and employee experience.", 70),
        ["Finance"] = new("finance", "#14B8A6", "Accounting, budgeting, payroll and financial planning.", 80),
        ["Marketing"] = new("marketing", "#F97316", "Brand, campaigns and demand generation.", 90),
        ["Customer Success"] = new("people", "#8B5CF6", "Onboarding, adoption and retention of customers.", 11),
        ["Service Delivery"] = new("operations", "#818CF8", "Delivers contracted services to clients.", 12),
        ["Project Management"] = new("operations", "#6366F1", "Plans and tracks cross-functional projects.", 13),
        ["Business Operations"] = new("operations", "#4F46E5", "Business planning, process and reporting.", 14)
    };

    private sealed record DemoPosition(string Dept, string Title, string JobTitle, string Level, int Headcount, string? ReportsToTitle);

    private static readonly DemoPosition[] DemoPositions =
    [
        new("Operations", "Operations Manager", "Operations Manager", "Manager", 1, null),
        new("Operations", "Operations Coordinator", "Operations Coordinator", "Individual Contributor", 3, "Operations Manager"),
        new("Operations", "Logistics Analyst", "Logistics Analyst", "Associate", 3, "Operations Manager"),
        new("Sales", "Sales Director", "Sales Director", "Director", 1, null),
        new("Sales", "Account Executive", "Account Executive", "Individual Contributor", 3, "Sales Director"),
        new("Sales", "Sales Development Representative", "Sales Development Representative", "Associate", 3, "Sales Director"),
        new("Administration", "Administration Manager", "Administration Manager", "Manager", 1, null),
        new("Administration", "HR Generalist", "HR Generalist", "Individual Contributor", 3, "Administration Manager"),
        new("Hospitality", "Guest Services Manager", "Guest Services Manager", "Manager", 1, null),
        new("Hospitality", "Front Desk Supervisor", "Front Desk Supervisor", "Supervisor", 2, "Guest Services Manager"),
        new("Hospitality", "Concierge", "Concierge", "Associate", 4, "Guest Services Manager"),
        new("IT", "IT Manager", "IT Manager", "Manager", 1, null),
        new("IT", "Software Engineer", "Software Engineer", "Individual Contributor", 4, "IT Manager"),
        new("IT", "Support Technician", "Support Technician", "Associate", 2, "IT Manager"),
        new("Finance", "Finance Manager", "Finance Manager", "Manager", 1, null)
    ];

    public async Task SeedDemoOrganizationIfEmptyAsync(Guid organizationId, Guid? actorUserId, bool isDevelopment, CancellationToken cancellationToken = default)
    {
        if (!isDevelopment) return;

        using var connection = OpenConnection();

        var positionCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM dbo.nh_hr_organizational_positions WHERE organization_id = @OrganizationId;",
            new { OrganizationId = organizationId }, cancellationToken: cancellationToken));
        if (positionCount > 0) return;

        var nestedCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM dbo.nh_hr_departments WHERE organization_id = @OrganizationId AND parent_department_id IS NOT NULL;",
            new { OrganizationId = organizationId }, cancellationToken: cancellationToken));
        if (nestedCount > 0) return;

        var employeeCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM dbo.nh_hr_employees WHERE organization_id = @OrganizationId AND archived_at IS NULL;",
            new { OrganizationId = organizationId }, cancellationToken: cancellationToken));
        if (employeeCount == 0) return; // never invent employees

        var today = DateTime.UtcNow.Date;

        using var tx = connection.BeginTransaction();
        try
        {
            await EnsureLevelsCoreAsync(connection, tx, organizationId, actorUserId, cancellationToken);

            // Enrich existing demo departments (never creates the five seeded by the employee demo data).
            foreach (var name in new[] { "Sales", "IT", "Administration", "Hospitality" })
                await EnrichDepartmentIfExistsAsync(connection, tx, organizationId, name, null, actorUserId, cancellationToken);

            // Operations is the parent of the sub-departments, so create it if absent.
            var operationsId = await GetOrCreateDepartmentAsync(connection, tx, organizationId, "Operations", null, actorUserId, cancellationToken);

            foreach (var name in new[] { "Technology", "People & Culture", "Finance", "Marketing" })
                await GetOrCreateDepartmentAsync(connection, tx, organizationId, name, null, actorUserId, cancellationToken);

            foreach (var name in new[] { "Customer Success", "Service Delivery", "Project Management", "Business Operations" })
                await GetOrCreateDepartmentAsync(connection, tx, organizationId, name, operationsId, actorUserId, cancellationToken);

            // Department heads: an existing employee of the department with a management-style title.
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE d SET d.head_employee_id = x.id, d.updated_at = @Now, d.updated_by = @ActorUserId
FROM dbo.nh_hr_departments d
CROSS APPLY (
    SELECT TOP 1 e.id
    FROM dbo.nh_hr_employees e
    JOIN dbo.nh_hr_job_titles jt ON jt.id = e.job_title_id AND jt.organization_id = e.organization_id
    WHERE e.organization_id = d.organization_id AND e.department_id = d.id
      AND e.archived_at IS NULL AND e.employment_status <> N'Terminated'
      AND (jt.name LIKE N'%Manager%' OR jt.name LIKE N'%Director%' OR jt.name LIKE N'%Head%' OR jt.name LIKE N'%Lead%')
    ORDER BY e.hire_date, e.employee_number
) x
WHERE d.organization_id = @OrganizationId AND d.head_employee_id IS NULL AND d.archived_at IS NULL;",
                new { OrganizationId = organizationId, Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId }, tx, cancellationToken: cancellationToken));

            var deptIds = (await connection.QueryAsync<(Guid Id, string Name)>(new CommandDefinition(
                "SELECT id, name FROM dbo.nh_hr_departments WHERE organization_id = @OrganizationId AND archived_at IS NULL;",
                new { OrganizationId = organizationId }, tx, cancellationToken: cancellationToken)))
                .ToDictionary(r => r.Name, r => r.Id, StringComparer.OrdinalIgnoreCase);

            var levelIds = (await connection.QueryAsync<(Guid Id, string Name)>(new CommandDefinition(
                "SELECT id, name FROM dbo.nh_hr_organizational_levels WHERE organization_id = @OrganizationId;",
                new { OrganizationId = organizationId }, tx, cancellationToken: cancellationToken)))
                .ToDictionary(r => r.Name, r => r.Id, StringComparer.OrdinalIgnoreCase);

            var titleIds = (await connection.QueryAsync<(Guid Id, string Name)>(new CommandDefinition(
                "SELECT id, name FROM dbo.nh_hr_job_titles WHERE organization_id = @OrganizationId;",
                new { OrganizationId = organizationId }, tx, cancellationToken: cancellationToken)))
                .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

            var hqLocationId = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
                @"SELECT TOP 1 id FROM dbo.nh_hr_work_locations
                  WHERE organization_id = @OrganizationId AND archived_at IS NULL AND is_active = 1
                  ORDER BY CASE WHEN name LIKE N'%HQ%' THEN 0 ELSE 1 END, name;",
                new { OrganizationId = organizationId }, tx, cancellationToken: cancellationToken));

            var positionIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            var assigned = 0;
            foreach (var demo in DemoPositions)
            {
                if (!deptIds.TryGetValue(demo.Dept, out var deptId)) continue;

                Guid? reportsTo = demo.ReportsToTitle != null && positionIds.TryGetValue(demo.Dept + "|" + demo.ReportsToTitle, out var parentPos)
                    ? parentPos
                    : null;

                var positionId = await InsertPositionCoreAsync(connection, tx, organizationId, new OrgPositionInsert(
                    demo.Title,
                    null,
                    titleIds.TryGetValue(demo.JobTitle, out var jtId) ? jtId : null,
                    deptId,
                    reportsTo,
                    hqLocationId,
                    levelIds.TryGetValue(demo.Level, out var lvlId) ? lvlId : null,
                    null,
                    demo.Headcount,
                    OrganizationPositionStatuses.Active,
                    today,
                    null), actorUserId, cancellationToken);
                positionIds[demo.Dept + "|" + demo.Title] = positionId;

                var employees = (await connection.QueryAsync<(Guid Id, DateTime? HireDate)>(new CommandDefinition(@"
SELECT TOP (@Take) e.id AS Id, e.hire_date AS HireDate
FROM dbo.nh_hr_employees e
JOIN dbo.nh_hr_job_titles jt ON jt.id = e.job_title_id AND jt.organization_id = e.organization_id
WHERE e.organization_id = @OrganizationId AND e.department_id = @DepartmentId AND jt.name = @JobTitle
  AND e.archived_at IS NULL AND e.employment_status IN (N'Active', N'OnLeave', N'Onboarding')
ORDER BY e.hire_date, e.employee_number;",
                    new { OrganizationId = organizationId, DepartmentId = deptId, JobTitle = demo.JobTitle, Take = demo.Headcount },
                    tx, cancellationToken: cancellationToken))).ToList();

                foreach (var emp in employees)
                {
                    await AssignPositionCoreAsync(connection, tx, organizationId, positionId, emp.Id, 100m, true,
                        emp.HireDate ?? today, actorUserId, cancellationToken);
                    assigned++;
                }
            }

            await InsertActivityCoreAsync(connection, tx, organizationId, "DemoSeeded", "Organization", null, null, null, null,
                "Demo organization structure created (development only)", null, null, today, actorUserId, "System", cancellationToken);

            tx.Commit();
            _logger.LogInformation(
                "Seeded demo organization for {OrganizationId}: {Positions} positions, {Assignments} assignments.",
                organizationId, positionIds.Count, assigned);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static async Task EnrichDepartmentIfExistsAsync(
        IDbConnection connection, IDbTransaction tx, Guid organizationId, string name, Guid? parentId, Guid? actorUserId, CancellationToken ct)
    {
        if (!DemoStyles.TryGetValue(name, out var style)) return;
        await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_departments
SET description = COALESCE(description, @Description), color = COALESCE(color, @Color), icon = COALESCE(icon, @Icon),
    display_order = CASE WHEN display_order = 0 THEN @Order ELSE display_order END,
    parent_department_id = COALESCE(parent_department_id, @ParentId),
    updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND name = @Name AND archived_at IS NULL;",
            new
            {
                OrganizationId = organizationId, Name = name, style.Description, style.Color, style.Icon, style.Order,
                ParentId = parentId, Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId
            }, tx, cancellationToken: ct));
    }

    private static async Task<Guid> GetOrCreateDepartmentAsync(
        IDbConnection connection, IDbTransaction tx, Guid organizationId, string name, Guid? parentId, Guid? actorUserId, CancellationToken ct)
    {
        var style = DemoStyles.TryGetValue(name, out var s) ? s : new DemoDeptStyle("default", "#6366F1", name, 100);
        var existing = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM dbo.nh_hr_departments WHERE organization_id = @OrganizationId AND name = @Name;",
            new { OrganizationId = organizationId, Name = name }, tx, cancellationToken: ct));

        if (existing.HasValue)
        {
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_departments
SET description = COALESCE(description, @Description), color = COALESCE(color, @Color), icon = COALESCE(icon, @Icon),
    display_order = CASE WHEN display_order = 0 THEN @Order ELSE display_order END,
    parent_department_id = COALESCE(parent_department_id, @ParentId),
    is_active = 1, archived_at = NULL, archived_by = NULL,
    updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND id = @Id;",
                new
                {
                    OrganizationId = organizationId, Id = existing.Value, style.Description, style.Color, style.Icon, style.Order,
                    ParentId = parentId, Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId
                }, tx, cancellationToken: ct));
            return existing.Value;
        }

        return await InsertDepartmentCoreAsync(connection, tx, organizationId,
            new OrgDepartmentInsert(name, null, style.Description, parentId, null, null, style.Color, style.Icon, style.Order),
            actorUserId, ct);
    }

    // -------------------------------------------------------------------------
    // Summary
    // -------------------------------------------------------------------------

    public async Task<OrganizationSummaryDto> GetSummaryAsync(Guid organizationId, DateTime today, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var p = new { OrganizationId = organizationId };

        var people = await LoadPeopleAsync(connection, organizationId, cancellationToken);
        var tree = PeopleTree.Build(people);

        var departmentRows = (await connection.QueryAsync<(int Total, int Teams)>(new CommandDefinition(@"
SELECT COUNT(1) AS Total, ISNULL(SUM(CASE WHEN parent_department_id IS NOT NULL THEN 1 ELSE 0 END), 0) AS Teams
FROM dbo.nh_hr_departments
WHERE organization_id = @OrganizationId AND archived_at IS NULL AND is_active = 1;", p, cancellationToken: cancellationToken))).First();

        var locationCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM dbo.nh_hr_work_locations WHERE organization_id = @OrganizationId AND archived_at IS NULL AND is_active = 1;",
            p, cancellationToken: cancellationToken));

        var seats = (await connection.QueryAsync<(int Approved, int Filled)>(new CommandDefinition($@"
SELECT p.approved_headcount AS Approved, {FilledCountSql} AS Filled
FROM dbo.nh_hr_organizational_positions p
WHERE p.organization_id = @OrganizationId AND {ActivePositionSql};", p, cancellationToken: cancellationToken))).ToList();

        var headsWithoutReports = await connection.QueryAsync<Guid>(new CommandDefinition(@"
SELECT DISTINCT d.head_employee_id
FROM dbo.nh_hr_departments d
JOIN dbo.nh_hr_employees e ON e.id = d.head_employee_id AND e.organization_id = d.organization_id
WHERE d.organization_id = @OrganizationId AND d.archived_at IS NULL AND d.is_active = 1
  AND d.head_employee_id IS NOT NULL AND " + ActiveEmployeeSql + ";", p, cancellationToken: cancellationToken));

        var withoutPosition = await connection.ExecuteScalarAsync<int>(new CommandDefinition($@"
SELECT COUNT(1) FROM dbo.nh_hr_employees e
WHERE e.organization_id = @OrganizationId AND {ActiveEmployeeSql}
  AND NOT EXISTS (SELECT 1 FROM dbo.nh_hr_employee_position_assignments a
                   WHERE a.organization_id = e.organization_id AND a.employee_id = e.id
                     AND a.end_date IS NULL AND a.status = N'Active');", p, cancellationToken: cancellationToken));

        var baseline = await connection.ExecuteScalarAsync<int>(new CommandDefinition($@"
SELECT COUNT(1) FROM dbo.nh_hr_employees e
WHERE e.organization_id = @OrganizationId AND {ActiveEmployeeSql}
  AND (e.hire_date IS NULL OR e.hire_date <= DATEADD(day, -30, @Today));",
            new { OrganizationId = organizationId, Today = today.Date }, cancellationToken: cancellationToken));

        var total = people.Count;
        var spans = tree.Children.Where(kv => kv.Value.Count > 0).Select(kv => kv.Value.Count).ToList();
        var headsOrphaned = headsWithoutReports.Count(id => !tree.Children.TryGetValue(id, out var kids) || kids.Count == 0);

        var approved = seats.Sum(s => s.Approved);
        var filled = seats.Sum(s => Math.Min(s.Filled, s.Approved));
        var occupied = seats.Count(s => s.Filled > 0);

        return new OrganizationSummaryDto
        {
            TotalEmployees = total,
            EmployeeGrowthPct = baseline > 0 ? Math.Round((total - baseline) * 100.0 / baseline, 1) : 0,
            TotalDepartments = departmentRows.Total,
            Teams = departmentRows.Teams,
            TotalPositions = seats.Count,
            OccupiedPositions = occupied,
            OpenPositions = seats.Count(s => s.Filled < s.Approved),
            ApprovedHeadcount = approved,
            ActualHeadcount = seats.Count > 0 ? filled : total,
            VacantHeadcount = Math.Max(0, approved - filled),
            HeadcountUtilizationPct = approved > 0 ? Math.Round(filled * 100.0 / approved, 1) : 0,
            AverageSpanOfControl = spans.Count > 0 ? Math.Round(spans.Average(), 1) : 0,
            TotalLocations = locationCount,
            EmployeesWithoutManager = tree.Roots.Count,
            ManagersWithoutReports = headsOrphaned,
            EmployeesWithoutPosition = withoutPosition,
            MaxDepth = tree.MaxDepth()
        };
    }

    // -------------------------------------------------------------------------
    // Charts
    // -------------------------------------------------------------------------

    public async Task<OrgDepartmentChartDto> GetDepartmentChartAsync(Guid organizationId, string? organizationName, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var rows = await LoadDepartmentRowsAsync(connection, organizationId, false, cancellationToken);
        var byId = rows.ToDictionary(r => r.Id);
        var childrenByParent = rows
            .Where(r => r.ParentDepartmentId.HasValue && byId.ContainsKey(r.ParentDepartmentId.Value) && r.ParentDepartmentId != r.Id)
            .GroupBy(r => r.ParentDepartmentId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var visited = new HashSet<Guid>();
        OrgChartDepartmentNodeDto Build(DeptRow row)
        {
            visited.Add(row.Id);
            var kids = childrenByParent.TryGetValue(row.Id, out var list)
                ? list.Where(k => !visited.Contains(k.Id)).Select(Build).ToList()
                : [];
            return new OrgChartDepartmentNodeDto
            {
                Id = row.Id,
                Name = row.Name,
                Code = row.Code,
                Description = row.Description,
                ParentDepartmentId = row.ParentDepartmentId,
                Color = row.Color,
                Icon = row.Icon,
                DisplayOrder = row.DisplayOrder,
                HeadEmployeeId = row.HeadEmployeeId,
                HeadName = row.HeadName,
                HeadPhotoUrl = row.HeadPhotoUrl,
                HeadJobTitle = row.HeadJobTitle,
                EmployeeCount = row.EmployeeCount,
                DirectEmployeeCount = row.DirectEmployeeCount,
                TeamCount = row.TeamCount,
                PositionCount = row.PositionCount,
                OpenPositionCount = row.OpenPositionCount,
                BudgetedHeadcount = row.BudgetedHeadcount,
                UtilizationPercent = row.UtilizationPercent,
                IsActive = row.IsActive,
                Members = row.Members,
                Children = kids
            };
        }

        var roots = rows
            .Where(r => !r.ParentDepartmentId.HasValue || !byId.ContainsKey(r.ParentDepartmentId.Value) || r.ParentDepartmentId == r.Id)
            .ToList();
        var nodes = roots.Select(Build).ToList();

        var people = await LoadPeopleAsync(connection, organizationId, cancellationToken);
        var tree = PeopleTree.Build(people);
        var ceo = tree.Roots
            .Select(r => (Row: r, Reports: tree.Children.TryGetValue(r.EmployeeId, out var k) ? k.Count : 0))
            .Where(x => x.Reports > 0)
            .OrderByDescending(x => x.Reports)
            .Select(x => ((PersonRow Row, int Reports)?)x)
            .FirstOrDefault();

        return new OrgDepartmentChartDto
        {
            Root = new OrgChartCompanyRootDto
            {
                Name = string.IsNullOrWhiteSpace(organizationName) ? "Organization" : organizationName.Trim(),
                CeoEmployeeId = ceo?.Row.EmployeeId,
                CeoName = ceo?.Row.Name,
                CeoJobTitle = ceo?.Row.JobTitle,
                TotalEmployees = people.Count,
                TotalDepartments = rows.Count,
                DirectReportCount = ceo?.Reports ?? nodes.Count
            },
            Departments = nodes
        };
    }

    public async Task<OrgPeopleChartDto> GetPeopleChartAsync(Guid organizationId, string? organizationName, Guid? rootEmployeeId, int? maxDepth, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var people = await LoadPeopleAsync(connection, organizationId, cancellationToken);
        var tree = PeopleTree.Build(people);

        var limit = maxDepth is > 0 ? maxDepth : null;
        var truncated = false;
        var reportCounts = new Dictionary<Guid, int>();

        OrgChartPeopleNodeDto Build(PersonRow row, int depth, HashSet<Guid> path)
        {
            path.Add(row.EmployeeId);
            tree.Children.TryGetValue(row.EmployeeId, out var kids);
            kids ??= [];
            var node = new OrgChartPeopleNodeDto
            {
                EmployeeId = row.EmployeeId,
                Name = row.Name,
                JobTitle = row.JobTitle,
                DepartmentId = row.DepartmentId,
                DepartmentName = row.DepartmentName,
                PhotoUrl = row.PhotoUrl,
                ManagerId = row.ManagerId,
                EmploymentStatus = row.EmploymentStatus,
                DirectReports = kids.Count,
                TotalReports = tree.TotalReports(row.EmployeeId, reportCounts)
            };

            if (limit.HasValue && depth >= limit.Value)
            {
                if (kids.Count > 0) truncated = true;
            }
            else
            {
                node.Children = kids.Where(k => !path.Contains(k.EmployeeId)).Select(k => Build(k, depth + 1, path)).ToList();
            }

            path.Remove(row.EmployeeId);
            return node;
        }

        List<PersonRow> rootRows;
        if (rootEmployeeId.HasValue)
        {
            var root = people.FirstOrDefault(p => p.EmployeeId == rootEmployeeId.Value);
            rootRows = root == null ? [] : [root];
        }
        else
        {
            rootRows = tree.Roots.ToList();
        }

        var roots = rootRows.Select(r => Build(r, 1, new HashSet<Guid>())).ToList();
        return new OrgPeopleChartDto
        {
            Roots = roots,
            Root = roots.FirstOrDefault(),
            Truncated = truncated
        };
    }

    public async Task<OrgPositionChartDto> GetPositionChartAsync(Guid organizationId, string? organizationName, Guid? departmentId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var sql = PositionSelectSql + PositionFromSql + @"
WHERE p.organization_id = @OrganizationId AND p.archived_at IS NULL AND p.status <> N'Archived'
  AND (@DepartmentId IS NULL OR p.department_id = @DepartmentId)
ORDER BY d.display_order, d.name, p.position_name;";

        var rows = (await connection.QueryAsync<OrgPositionListItemDto>(new CommandDefinition(sql,
            new { OrganizationId = organizationId, DepartmentId = departmentId }, cancellationToken: cancellationToken))).ToList();
        await FillOccupantsAsync(connection, organizationId, rows, cancellationToken);

        return new OrgPositionChartDto
        {
            Positions = rows.Select(r => new OrgChartPositionNodeDto
            {
                Id = r.Id,
                Title = r.Title,
                Code = r.Code,
                DepartmentId = r.DepartmentId,
                DepartmentName = r.DepartmentName,
                ReportsToPositionId = r.ReportsToPositionId,
                LevelName = r.LevelName,
                Status = r.Status,
                Headcount = r.Headcount,
                FilledCount = r.FilledCount,
                OpenCount = r.OpenCount,
                Occupants = r.Occupants
            }).ToList()
        };
    }

    // -------------------------------------------------------------------------
    // Departments
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<OrgDepartmentListItemDto>> ListDepartmentsAsync(Guid organizationId, bool includeArchived, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var rows = await LoadDepartmentRowsAsync(connection, organizationId, includeArchived, cancellationToken);
        return rows.Select(r => (OrgDepartmentListItemDto)r.ToDetail()).ToList();
    }

    public async Task<OrgDepartmentDetailDto?> GetDepartmentAsync(Guid organizationId, Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var rows = await LoadDepartmentRowsAsync(connection, organizationId, true, cancellationToken);
        return rows.FirstOrDefault(r => r.Id == id)?.ToDetail();
    }

    public async Task<OrgDepartmentSummaryDto?> GetDepartmentSummaryAsync(Guid organizationId, Guid id, DateTime today, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var rows = await LoadDepartmentRowsAsync(connection, organizationId, true, cancellationToken);
        var row = rows.FirstOrDefault(r => r.Id == id);
        if (row == null) return null;

        // Sub-tree (department + descendants).
        var ids = new List<Guid> { row.Id };
        var seen = new HashSet<Guid> { row.Id };
        for (var i = 0; i < ids.Count; i++)
        {
            foreach (var child in rows.Where(r => r.ParentDepartmentId == ids[i] && seen.Add(r.Id)))
                ids.Add(child.Id);
        }

        var window = today.Date.AddDays(-RecentWindowDays);
        var counts = await connection.QuerySingleAsync<(int Hires, int Exits)>(new CommandDefinition(@"
SELECT ISNULL(SUM(CASE WHEN e.hire_date >= @Since AND e.employment_status <> N'Terminated' THEN 1 ELSE 0 END), 0) AS Hires,
       ISNULL(SUM(CASE WHEN e.termination_date >= @Since THEN 1 ELSE 0 END), 0) AS Exits
FROM dbo.nh_hr_employees e
WHERE e.organization_id = @OrganizationId AND e.archived_at IS NULL AND e.department_id IN @Ids;",
            new { OrganizationId = organizationId, Ids = ids, Since = window }, cancellationToken: cancellationToken));

        var spanRows = (await connection.QueryAsync<int>(new CommandDefinition($@"
SELECT COUNT(1)
FROM dbo.nh_hr_employees r
JOIN dbo.nh_hr_employees e ON e.id = r.manager_id AND e.organization_id = r.organization_id
WHERE r.organization_id = @OrganizationId AND r.archived_at IS NULL AND r.employment_status <> N'Terminated'
  AND {ActiveEmployeeSql} AND e.department_id IN @Ids
GROUP BY r.manager_id;",
            new { OrganizationId = organizationId, Ids = ids }, cancellationToken: cancellationToken))).ToList();

        var head = row.HeadEmployeeId.HasValue
            ? await connection.QuerySingleOrDefaultAsync<(string? Email, string? Phone)>(new CommandDefinition(
                "SELECT work_email AS Email, work_phone AS Phone FROM dbo.nh_hr_employees WHERE organization_id = @OrganizationId AND id = @Id;",
                new { OrganizationId = organizationId, Id = row.HeadEmployeeId.Value }, cancellationToken: cancellationToken))
            : default;

        var filledPositions = await connection.ExecuteScalarAsync<int>(new CommandDefinition($@"
SELECT COUNT(1) FROM dbo.nh_hr_organizational_positions p
WHERE p.organization_id = @OrganizationId AND p.department_id IN @Ids AND {ActivePositionSql}
  AND {FilledCountSql} > 0;",
            new { OrganizationId = organizationId, Ids = ids }, cancellationToken: cancellationToken));

        return new OrgDepartmentSummaryDto
        {
            DepartmentId = row.Id,
            Name = row.Name,
            Description = row.Description,
            Icon = row.Icon,
            Color = row.Color,
            EmployeeCount = row.EmployeeCount,
            DirectEmployeeCount = row.DirectEmployeeCount,
            SubDepartmentCount = row.TeamCount,
            PositionCount = row.PositionCount,
            OpenPositionCount = row.OpenPositionCount,
            FilledPositionCount = filledPositions,
            BudgetedHeadcount = row.BudgetedHeadcount,
            UtilizationPercent = row.UtilizationPercent,
            AverageSpanOfControl = spanRows.Count > 0 ? Math.Round(spanRows.Average(), 1) : null,
            RecentHires = counts.Hires,
            RecentExits = counts.Exits,
            HeadEmployeeId = row.HeadEmployeeId,
            HeadName = row.HeadName,
            HeadJobTitle = row.HeadJobTitle,
            HeadPhotoUrl = row.HeadPhotoUrl,
            HeadWorkEmail = head.Email,
            HeadWorkPhone = head.Phone
        };
    }

    public async Task<Guid> InsertDepartmentAsync(Guid organizationId, OrgDepartmentInsert row, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        try
        {
            return await InsertDepartmentCoreAsync(connection, null, organizationId, row, actorUserId, cancellationToken);
        }
        catch (SqlException ex) when (IsDuplicateKey(ex))
        {
            throw EmployeeServiceException.Conflict("A department with that name already exists.");
        }
    }

    private static async Task<Guid> InsertDepartmentCoreAsync(
        IDbConnection connection, IDbTransaction? tx, Guid organizationId, OrgDepartmentInsert row, Guid? actorUserId, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_departments
    (id, organization_id, name, code, description, parent_department_id, head_employee_id, cost_center,
     color, icon, display_order, is_active, created_at, created_by)
VALUES
    (@Id, @OrganizationId, @Name, @Code, @Description, @ParentDepartmentId, @HeadEmployeeId, @CostCenter,
     @Color, @Icon, @DisplayOrder, 1, @Now, @ActorUserId);",
            new
            {
                Id = id,
                OrganizationId = organizationId,
                Name = row.Name.Trim(),
                Code = Trim(row.Code),
                Description = Trim(row.Description),
                row.ParentDepartmentId,
                row.HeadEmployeeId,
                CostCenter = Trim(row.CostCenter),
                row.Color,
                Icon = string.IsNullOrWhiteSpace(row.Icon) ? "default" : row.Icon,
                row.DisplayOrder,
                Now = DateTimeOffset.UtcNow,
                ActorUserId = actorUserId
            }, tx, cancellationToken: ct));
        return id;
    }

    public async Task<bool> UpdateDepartmentAsync(Guid organizationId, Guid id, OrgDepartmentInsert row, bool? isActive, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
UPDATE dbo.nh_hr_departments
SET name = @Name, code = @Code, description = @Description, parent_department_id = @ParentDepartmentId,
    head_employee_id = @HeadEmployeeId, cost_center = @CostCenter, color = @Color, icon = @Icon,
    display_order = @DisplayOrder, is_active = COALESCE(@IsActive, is_active),
    updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND id = @Id AND archived_at IS NULL;";

        using var connection = OpenConnection();
        try
        {
            var rows = await connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                OrganizationId = organizationId,
                Id = id,
                Name = row.Name.Trim(),
                Code = Trim(row.Code),
                Description = Trim(row.Description),
                row.ParentDepartmentId,
                row.HeadEmployeeId,
                CostCenter = Trim(row.CostCenter),
                row.Color,
                Icon = string.IsNullOrWhiteSpace(row.Icon) ? "default" : row.Icon,
                row.DisplayOrder,
                IsActive = isActive,
                Now = DateTimeOffset.UtcNow,
                ActorUserId = actorUserId
            }, cancellationToken: cancellationToken));
            return rows > 0;
        }
        catch (SqlException ex) when (IsDuplicateKey(ex))
        {
            throw EmployeeServiceException.Conflict("A department with that name already exists.");
        }
    }

    public async Task<bool> MoveDepartmentAsync(Guid organizationId, Guid id, Guid? newParentId, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        if (await WouldCreateDepartmentCycleAsync(organizationId, id, newParentId, cancellationToken))
            throw EmployeeServiceException.Validation("A department cannot be moved under itself or one of its sub-departments.");

        const string sql = @"
UPDATE dbo.nh_hr_departments
SET parent_department_id = @NewParentId, updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND id = @Id AND archived_at IS NULL;";

        using var connection = OpenConnection();
        var rows = await connection.ExecuteAsync(new CommandDefinition(sql,
            new { OrganizationId = organizationId, Id = id, NewParentId = newParentId, Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId },
            cancellationToken: cancellationToken));
        return rows > 0;
    }

    public async Task<bool> ArchiveDepartmentAsync(Guid organizationId, Guid id, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
UPDATE dbo.nh_hr_departments
SET archived_at = @Now, archived_by = @ActorUserId, is_active = 0, updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND id = @Id AND archived_at IS NULL;";

        using var connection = OpenConnection();
        var rows = await connection.ExecuteAsync(new CommandDefinition(sql,
            new { OrganizationId = organizationId, Id = id, Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId },
            cancellationToken: cancellationToken));
        return rows > 0;
    }

    public async Task<bool> WouldCreateDepartmentCycleAsync(Guid organizationId, Guid departmentId, Guid? newParentId, CancellationToken cancellationToken = default)
    {
        if (!newParentId.HasValue) return false;
        if (newParentId.Value == departmentId) return true;

        const string sql = "SELECT parent_department_id FROM dbo.nh_hr_departments WHERE organization_id = @OrganizationId AND id = @Id;";
        using var connection = OpenConnection();
        var visited = new HashSet<Guid>();
        Guid? current = newParentId;
        while (current.HasValue)
        {
            if (current.Value == departmentId) return true;
            if (!visited.Add(current.Value)) return false;
            current = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(sql,
                new { OrganizationId = organizationId, Id = current.Value }, cancellationToken: cancellationToken));
        }
        return false;
    }

    public async Task<int> CountActiveEmployeesInDepartmentAsync(Guid organizationId, Guid departmentId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition($@"
SELECT COUNT(1) FROM dbo.nh_hr_employees e
WHERE e.organization_id = @OrganizationId AND e.department_id = @DepartmentId AND {ActiveEmployeeSql};",
            new { OrganizationId = organizationId, DepartmentId = departmentId }, cancellationToken: cancellationToken));
    }

    public async Task<int> CountActivePositionsInDepartmentAsync(Guid organizationId, Guid departmentId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition($@"
SELECT COUNT(1) FROM dbo.nh_hr_organizational_positions p
WHERE p.organization_id = @OrganizationId AND p.department_id = @DepartmentId AND {ActivePositionSql};",
            new { OrganizationId = organizationId, DepartmentId = departmentId }, cancellationToken: cancellationToken));
    }

    // -------------------------------------------------------------------------
    // Positions
    // -------------------------------------------------------------------------

    public async Task<PagedResult<OrgPositionListItemDto>> ListPositionsAsync(Guid organizationId, OrgPositionListQuery query, CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 500);
        var offset = (page - 1) * pageSize;

        var status = OrganizationValidation.NormalizePositionStatus(query.Status);
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var searchLike = search == null ? null : "%" + EscapeLike(search) + "%";
        var occupancy = query.Occupancy?.Trim().ToLowerInvariant();

        var where = new StringBuilder("WHERE p.organization_id = @OrganizationId");
        where.Append(status == OrganizationPositionStatuses.Archived
            ? " AND (p.archived_at IS NOT NULL OR p.status = N'Archived')"
            : " AND p.archived_at IS NULL AND p.status <> N'Archived'");
        if (status != null && status != OrganizationPositionStatuses.Archived) where.Append(" AND p.status = @Status");
        where.Append(" AND (@DepartmentId IS NULL OR p.department_id = @DepartmentId)");
        where.Append(" AND (@JobTitleId IS NULL OR p.job_title_id = @JobTitleId)");
        where.Append(" AND (@LevelId IS NULL OR p.organizational_level_id = @LevelId)");
        where.Append(" AND (@LocationId IS NULL OR p.work_location_id = @LocationId)");
        where.Append(@" AND (@SearchLike IS NULL OR p.position_name LIKE @SearchLike ESCAPE '\'
                          OR p.position_code LIKE @SearchLike ESCAPE '\'
                          OR d.name LIKE @SearchLike ESCAPE '\'
                          OR jt.name LIKE @SearchLike ESCAPE '\')");
        where.Append(occupancy switch
        {
            "vacant" or "open" => $" AND {FilledCountSql} < p.approved_headcount",
            "filled" or "occupied" => $" AND {FilledCountSql} > 0",
            "full" => $" AND {FilledCountSql} >= p.approved_headcount",
            _ => ""
        });

        var parameters = new
        {
            OrganizationId = organizationId,
            Status = status,
            query.DepartmentId,
            query.JobTitleId,
            LevelId = query.ResolvedLevelId,
            LocationId = query.ResolvedLocationId,
            SearchLike = searchLike,
            Offset = offset,
            PageSize = pageSize
        };

        var countSql = "SELECT COUNT(1) " + PositionFromSql + " " + where + ";";
        var pageSql = PositionSelectSql + PositionFromSql + " " + where + @"
ORDER BY d.display_order, d.name, p.position_name
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";

        using var connection = OpenConnection();
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(countSql, parameters, cancellationToken: cancellationToken));
        var rows = (await connection.QueryAsync<OrgPositionListItemDto>(new CommandDefinition(pageSql, parameters, cancellationToken: cancellationToken))).ToList();
        await FillOccupantsAsync(connection, organizationId, rows, cancellationToken);

        return new PagedResult<OrgPositionListItemDto> { Items = rows, Page = page, PageSize = pageSize, Total = total };
    }

    public async Task<OrgPositionDetailDto?> GetPositionAsync(Guid organizationId, Guid id, CancellationToken cancellationToken = default)
    {
        var sql = PositionSelectSql + @",
       p.description AS Description, p.employment_type_id AS EmploymentTypeId, et.name AS EmploymentType,
       p.end_date AS EndDate, p.created_at AS CreatedAt" + PositionFromSql + @"
LEFT JOIN dbo.nh_hr_employment_types et ON et.id = p.employment_type_id AND et.organization_id = p.organization_id
WHERE p.organization_id = @OrganizationId AND p.id = @Id;";

        using var connection = OpenConnection();
        var row = await connection.QuerySingleOrDefaultAsync<OrgPositionDetailDto>(new CommandDefinition(sql,
            new { OrganizationId = organizationId, Id = id }, cancellationToken: cancellationToken));
        if (row == null) return null;

        await FillOccupantsAsync(connection, organizationId, [row], cancellationToken);
        return row;
    }

    public async Task<Guid> InsertPositionAsync(Guid organizationId, OrgPositionInsert row, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        return await InsertPositionCoreAsync(connection, null, organizationId, row, actorUserId, cancellationToken);
    }

    private static async Task<Guid> InsertPositionCoreAsync(
        IDbConnection connection, IDbTransaction? tx, Guid organizationId, OrgPositionInsert row, Guid? actorUserId, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_organizational_positions
    (id, organization_id, position_code, position_name, job_title_id, department_id, reports_to_position_id,
     work_location_id, organizational_level_id, employment_type_id, approved_headcount, status, effective_date,
     description, created_at, created_by)
VALUES
    (@Id, @OrganizationId, @PositionCode, @PositionName, @JobTitleId, @DepartmentId, @ReportsToPositionId,
     @WorkLocationId, @OrganizationalLevelId, @EmploymentTypeId, @ApprovedHeadcount, @Status, @EffectiveDate,
     @Description, @Now, @ActorUserId);",
            new
            {
                Id = id,
                OrganizationId = organizationId,
                PositionCode = Trim(row.PositionCode),
                PositionName = row.PositionName.Trim(),
                row.JobTitleId,
                row.DepartmentId,
                row.ReportsToPositionId,
                row.WorkLocationId,
                row.OrganizationalLevelId,
                row.EmploymentTypeId,
                row.ApprovedHeadcount,
                row.Status,
                EffectiveDate = row.EffectiveDate?.Date,
                Description = Trim(row.Description),
                Now = DateTimeOffset.UtcNow,
                ActorUserId = actorUserId
            }, tx, cancellationToken: ct));
        return id;
    }

    public async Task<bool> UpdatePositionAsync(Guid organizationId, Guid id, OrgPositionInsert row, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
UPDATE dbo.nh_hr_organizational_positions
SET position_code = @PositionCode, position_name = @PositionName, job_title_id = @JobTitleId,
    department_id = @DepartmentId, reports_to_position_id = @ReportsToPositionId,
    work_location_id = @WorkLocationId, organizational_level_id = @OrganizationalLevelId,
    employment_type_id = @EmploymentTypeId, approved_headcount = @ApprovedHeadcount, status = @Status,
    effective_date = COALESCE(@EffectiveDate, effective_date), description = @Description,
    updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND id = @Id AND archived_at IS NULL;";

        using var connection = OpenConnection();
        var rows = await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            OrganizationId = organizationId,
            Id = id,
            PositionCode = Trim(row.PositionCode),
            PositionName = row.PositionName.Trim(),
            row.JobTitleId,
            row.DepartmentId,
            row.ReportsToPositionId,
            row.WorkLocationId,
            row.OrganizationalLevelId,
            row.EmploymentTypeId,
            row.ApprovedHeadcount,
            row.Status,
            EffectiveDate = row.EffectiveDate?.Date,
            Description = Trim(row.Description),
            Now = DateTimeOffset.UtcNow,
            ActorUserId = actorUserId
        }, cancellationToken: cancellationToken));
        return rows > 0;
    }

    public async Task<bool> ArchivePositionAsync(Guid organizationId, Guid id, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var rows = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_organizational_positions
SET archived_at = @Now, archived_by = @ActorUserId, status = N'Archived', end_date = @Today,
    updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND id = @Id AND archived_at IS NULL;",
                new { OrganizationId = organizationId, Id = id, Now = now, ActorUserId = actorUserId, Today = now.UtcDateTime.Date },
                tx, cancellationToken: cancellationToken));

            if (rows > 0)
            {
                // Direct reports of the archived position become top-level so the chart stays connected.
                await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_organizational_positions
SET reports_to_position_id = NULL, updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND reports_to_position_id = @Id;",
                    new { OrganizationId = organizationId, Id = id, Now = now, ActorUserId = actorUserId }, tx, cancellationToken: cancellationToken));
            }

            tx.Commit();
            return rows > 0;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<bool> MovePositionAsync(Guid organizationId, Guid id, Guid? newDepartmentId, Guid? newReportsTo, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        if (await WouldCreatePositionCycleAsync(organizationId, id, newReportsTo, cancellationToken))
            throw EmployeeServiceException.Validation("A position cannot report to itself or to one of its own reports.");

        const string sql = @"
UPDATE dbo.nh_hr_organizational_positions
SET department_id = COALESCE(@NewDepartmentId, department_id), reports_to_position_id = @NewReportsTo,
    updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND id = @Id AND archived_at IS NULL;";

        using var connection = OpenConnection();
        var rows = await connection.ExecuteAsync(new CommandDefinition(sql,
            new { OrganizationId = organizationId, Id = id, NewDepartmentId = newDepartmentId, NewReportsTo = newReportsTo, Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId },
            cancellationToken: cancellationToken));
        return rows > 0;
    }

    public async Task<bool> WouldCreatePositionCycleAsync(Guid organizationId, Guid positionId, Guid? newReportsTo, CancellationToken cancellationToken = default)
    {
        if (!newReportsTo.HasValue) return false;
        if (newReportsTo.Value == positionId) return true;

        const string sql = "SELECT reports_to_position_id FROM dbo.nh_hr_organizational_positions WHERE organization_id = @OrganizationId AND id = @Id;";
        using var connection = OpenConnection();
        var visited = new HashSet<Guid>();
        Guid? current = newReportsTo;
        while (current.HasValue)
        {
            if (current.Value == positionId) return true;
            if (!visited.Add(current.Value)) return false;
            current = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(sql,
                new { OrganizationId = organizationId, Id = current.Value }, cancellationToken: cancellationToken));
        }
        return false;
    }

    public async Task<Guid> AssignPositionAsync(
        Guid organizationId, Guid positionId, Guid employeeId, decimal allocationPct, bool isPrimary, DateTime effectiveDate, Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        try
        {
            var id = await AssignPositionCoreAsync(connection, tx, organizationId, positionId, employeeId, allocationPct, isPrimary, effectiveDate, actorUserId, cancellationToken);
            tx.Commit();
            return id;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private sealed class PositionState
    {
        public int ApprovedHeadcount { get; set; }
        public bool IsArchived { get; set; }
        public int Filled { get; set; }
    }

    private static async Task<Guid> AssignPositionCoreAsync(
        IDbConnection connection, IDbTransaction tx, Guid organizationId, Guid positionId, Guid employeeId,
        decimal allocationPct, bool isPrimary, DateTime effectiveDate, Guid? actorUserId, CancellationToken ct)
    {
        var state = await connection.QuerySingleOrDefaultAsync<PositionState>(new CommandDefinition($@"
SELECT p.approved_headcount AS ApprovedHeadcount,
       CAST(CASE WHEN p.archived_at IS NULL AND p.status <> N'Archived' THEN 0 ELSE 1 END AS bit) AS IsArchived,
       {FilledCountSql} AS Filled
FROM dbo.nh_hr_organizational_positions p
WHERE p.organization_id = @OrganizationId AND p.id = @PositionId;",
            new { OrganizationId = organizationId, PositionId = positionId }, tx, cancellationToken: ct));
        if (state == null) throw EmployeeServiceException.NotFound("Position not found.");
        if (state.IsArchived) throw EmployeeServiceException.Validation("Archived positions cannot be assigned.");

        var employeeExists = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM dbo.nh_hr_employees WHERE organization_id = @OrganizationId AND id = @EmployeeId AND archived_at IS NULL;",
            new { OrganizationId = organizationId, EmployeeId = employeeId }, tx, cancellationToken: ct));
        if (employeeExists == 0) throw EmployeeServiceException.NotFound("Employee not found.");

        var duplicate = await connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(1) FROM dbo.nh_hr_employee_position_assignments
WHERE organization_id = @OrganizationId AND position_id = @PositionId AND employee_id = @EmployeeId
  AND end_date IS NULL AND status = N'Active';",
            new { OrganizationId = organizationId, PositionId = positionId, EmployeeId = employeeId }, tx, cancellationToken: ct));
        if (duplicate > 0) throw EmployeeServiceException.Conflict("The employee is already assigned to this position.");

        if (state.Filled >= state.ApprovedHeadcount)
            throw EmployeeServiceException.Validation("The position has no open headcount. Increase the approved headcount first.");

        var now = DateTimeOffset.UtcNow;
        var effective = effectiveDate.Date;

        if (isPrimary)
        {
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_employee_position_assignments
SET end_date = @EndDate, status = N'Ended', updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND employee_id = @EmployeeId AND is_primary = 1
  AND end_date IS NULL AND status = N'Active' AND position_id <> @PositionId;",
                new { OrganizationId = organizationId, EmployeeId = employeeId, PositionId = positionId, EndDate = effective, Now = now, ActorUserId = actorUserId },
                tx, cancellationToken: ct));
        }

        var id = Guid.NewGuid();
        await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_employee_position_assignments
    (id, organization_id, employee_id, position_id, allocation_pct, is_primary, effective_date, end_date, status, created_at, created_by)
VALUES
    (@Id, @OrganizationId, @EmployeeId, @PositionId, @AllocationPct, @IsPrimary, @EffectiveDate, NULL, N'Active', @Now, @ActorUserId);",
            new
            {
                Id = id,
                OrganizationId = organizationId,
                EmployeeId = employeeId,
                PositionId = positionId,
                AllocationPct = Math.Clamp(allocationPct <= 0 ? 100m : allocationPct, 0.01m, 100m),
                IsPrimary = isPrimary,
                EffectiveDate = effective,
                Now = now,
                ActorUserId = actorUserId
            }, tx, cancellationToken: ct));
        return id;
    }

    public async Task<bool> UnassignPositionAsync(
        Guid organizationId, Guid positionId, Guid? assignmentId, Guid? employeeId, DateTime endDate, Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (!assignmentId.HasValue && !employeeId.HasValue) return false;

        const string sql = @"
UPDATE dbo.nh_hr_employee_position_assignments
SET end_date = CASE WHEN effective_date > @EndDate THEN effective_date ELSE @EndDate END,
    status = N'Ended', updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND position_id = @PositionId AND end_date IS NULL
  AND (@AssignmentId IS NULL OR id = @AssignmentId)
  AND (@EmployeeId IS NULL OR employee_id = @EmployeeId);";

        using var connection = OpenConnection();
        var rows = await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            OrganizationId = organizationId,
            PositionId = positionId,
            AssignmentId = assignmentId,
            EmployeeId = employeeId,
            EndDate = endDate.Date,
            Now = DateTimeOffset.UtcNow,
            ActorUserId = actorUserId
        }, cancellationToken: cancellationToken));
        return rows > 0;
    }

    // -------------------------------------------------------------------------
    // Levels / locations
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<OrgLevelDto>> ListLevelsAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT l.id AS Id, l.name AS Name, l.description AS Description, l.level_order AS LevelOrder, l.level_order AS Rank,
       l.is_active AS IsActive,
       (SELECT COUNT(1) FROM dbo.nh_hr_organizational_positions p
         WHERE p.organization_id = l.organization_id AND p.organizational_level_id = l.id
           AND p.archived_at IS NULL AND p.status <> N'Archived') AS PositionCount
FROM dbo.nh_hr_organizational_levels l
WHERE l.organization_id = @OrganizationId AND l.is_active = 1
ORDER BY l.level_order, l.name;";

        using var connection = OpenConnection();
        var rows = await connection.QueryAsync<OrgLevelDto>(new CommandDefinition(sql,
            new { OrganizationId = organizationId }, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    public async Task<Guid> UpsertLevelAsync(Guid organizationId, string name, string? description, int levelOrder, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        return await UpsertLevelCoreAsync(connection, null, organizationId, name, description, levelOrder, actorUserId, cancellationToken);
    }

    public async Task<IReadOnlyList<OrgLocationListItemDto>> ListLocationsAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var sql = LocationSelectSql + @"
WHERE l.organization_id = @OrganizationId AND l.archived_at IS NULL
ORDER BY l.is_headquarters DESC, l.name;";

        using var connection = OpenConnection();
        var rows = await connection.QueryAsync<OrgLocationListItemDto>(new CommandDefinition(sql,
            new { OrganizationId = organizationId }, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    private static string? ResolveLocationType(CreateOrgLocationRequest request)
    {
        if (request.IsRemote == true) return OrganizationLocationTypes.Remote;
        return OrganizationValidation.NormalizeLocationType(request.LocationType);
    }

    public async Task<Guid> UpsertLocationAsync(Guid organizationId, CreateOrgLocationRequest request, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw EmployeeServiceException.Validation("Name is required.");

        var name = request.Name.Trim();
        var locationType = ResolveLocationType(request);
        var now = DateTimeOffset.UtcNow;

        using var connection = OpenConnection();
        var existing = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM dbo.nh_hr_work_locations WHERE organization_id = @OrganizationId AND name = @Name;",
            new { OrganizationId = organizationId, Name = name }, cancellationToken: cancellationToken));

        var parameters = new
        {
            OrganizationId = organizationId,
            Id = existing ?? Guid.NewGuid(),
            Name = name,
            City = Trim(request.City),
            State = Trim(request.State),
            Country = Trim(request.Country),
            Address = Trim(request.ResolvedAddress),
            Timezone = Trim(request.Timezone),
            LocationType = locationType,
            IsHeadquarters = request.IsHeadquarters,
            Now = now,
            ActorUserId = actorUserId
        };

        if (existing.HasValue)
        {
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_work_locations
SET is_active = 1, archived_at = NULL,
    city = COALESCE(@City, city), state = COALESCE(@State, state), country = COALESCE(@Country, country),
    address_line1 = COALESCE(@Address, address_line1), timezone = COALESCE(@Timezone, timezone),
    location_type = COALESCE(@LocationType, location_type), is_headquarters = COALESCE(@IsHeadquarters, is_headquarters),
    updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND id = @Id;", parameters, cancellationToken: cancellationToken));
            return existing.Value;
        }

        await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_work_locations
    (id, organization_id, name, city, state, country, address_line1, timezone, location_type, is_headquarters, is_active, created_at, created_by)
VALUES
    (@Id, @OrganizationId, @Name, @City, @State, @Country, @Address, @Timezone, ISNULL(@LocationType, N'Office'), ISNULL(@IsHeadquarters, 0), 1, @Now, @ActorUserId);",
            parameters, cancellationToken: cancellationToken));
        return parameters.Id;
    }

    public async Task<bool> UpdateLocationAsync(Guid organizationId, Guid id, UpdateOrgLocationRequest request, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
UPDATE dbo.nh_hr_work_locations
SET name = @Name, city = @City, state = @State, country = @Country, address_line1 = @Address, timezone = @Timezone,
    location_type = COALESCE(@LocationType, location_type), is_headquarters = COALESCE(@IsHeadquarters, is_headquarters),
    is_active = COALESCE(@IsActive, is_active), updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND id = @Id AND archived_at IS NULL;";

        using var connection = OpenConnection();
        try
        {
            var rows = await connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                OrganizationId = organizationId,
                Id = id,
                Name = request.Name.Trim(),
                City = Trim(request.City),
                State = Trim(request.State),
                Country = Trim(request.Country),
                Address = Trim(request.ResolvedAddress),
                Timezone = Trim(request.Timezone),
                LocationType = ResolveLocationType(request),
                request.IsHeadquarters,
                request.IsActive,
                Now = DateTimeOffset.UtcNow,
                ActorUserId = actorUserId
            }, cancellationToken: cancellationToken));
            return rows > 0;
        }
        catch (SqlException ex) when (IsDuplicateKey(ex))
        {
            throw EmployeeServiceException.Conflict("A location with that name already exists.");
        }
    }

    // -------------------------------------------------------------------------
    // Reporting
    // -------------------------------------------------------------------------

    public async Task<OrgReportingOverviewDto> GetReportingOverviewAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var people = await LoadPeopleAsync(connection, organizationId, cancellationToken);
        var tree = PeopleTree.Build(people);
        var reportCounts = new Dictionary<Guid, int>();

        OrgChartPeopleNodeDto ToNode(PersonRow row)
        {
            var directs = tree.Children.TryGetValue(row.EmployeeId, out var kids) ? kids.Count : 0;
            return new OrgChartPeopleNodeDto
            {
                EmployeeId = row.EmployeeId,
                Name = row.Name,
                JobTitle = row.JobTitle,
                DepartmentId = row.DepartmentId,
                DepartmentName = row.DepartmentName,
                PhotoUrl = row.PhotoUrl,
                ManagerId = row.ManagerId,
                EmploymentStatus = row.EmploymentStatus,
                DirectReports = directs,
                TotalReports = tree.TotalReports(row.EmployeeId, reportCounts)
            };
        }

        var managers = people
            .Where(p => tree.Children.TryGetValue(p.EmployeeId, out var kids) && kids.Count > 0)
            .Select(ToNode)
            .OrderByDescending(n => n.DirectReports)
            .ThenBy(n => n.Name)
            .ToList();

        // Orphans: people with neither a manager nor any reports (not part of any reporting line).
        var orphans = tree.Roots
            .Where(r => !tree.Children.TryGetValue(r.EmployeeId, out var kids) || kids.Count == 0)
            .Select(ToNode)
            .ToList();

        var spans = managers.Select(m => m.DirectReports).ToList();
        return new OrgReportingOverviewDto
        {
            AverageSpanOfControl = spans.Count > 0 ? Math.Round(spans.Average(), 1) : 0,
            MaxSpanOfControl = spans.Count > 0 ? spans.Max() : 0,
            MaxDepth = tree.MaxDepth(),
            ManagerCount = managers.Count,
            OrphanCount = orphans.Count,
            Managers = managers,
            Orphans = orphans
        };
    }

    public async Task SetEmployeeManagerAsync(Guid organizationId, Guid employeeId, Guid? managerId, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
UPDATE dbo.nh_hr_employees
SET manager_id = @ManagerId, updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND id = @EmployeeId;";

        using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(sql,
            new { OrganizationId = organizationId, EmployeeId = employeeId, ManagerId = managerId, Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId },
            cancellationToken: cancellationToken));
    }

    // -------------------------------------------------------------------------
    // Activity
    // -------------------------------------------------------------------------

    public async Task InsertActivityAsync(
        Guid organizationId, string changeType, string entityType, Guid? entityId, Guid? departmentId, Guid? employeeId, Guid? positionId,
        string summary, string? previousValue, string? newValue, DateTime? effectiveDate, Guid? actorUserId, string? actorName,
        CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        await InsertActivityCoreAsync(connection, null, organizationId, changeType, entityType, entityId, departmentId, employeeId, positionId,
            summary, previousValue, newValue, effectiveDate, actorUserId, actorName, cancellationToken);
    }

    private static async Task InsertActivityCoreAsync(
        IDbConnection connection, IDbTransaction? tx, Guid organizationId, string changeType, string entityType, Guid? entityId,
        Guid? departmentId, Guid? employeeId, Guid? positionId, string summary, string? previousValue, string? newValue,
        DateTime? effectiveDate, Guid? actorUserId, string? actorName, CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_organization_activity
    (id, organization_id, change_type, entity_type, entity_id, department_id, employee_id, position_id,
     summary, previous_value, new_value, effective_date, created_at, created_by, created_by_name)
VALUES
    (@Id, @OrganizationId, @ChangeType, @EntityType, @EntityId, @DepartmentId, @EmployeeId, @PositionId,
     @Summary, @PreviousValue, @NewValue, @EffectiveDate, @Now, @ActorUserId, @ActorName);",
            new
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                ChangeType = Truncate(changeType, 80),
                EntityType = Truncate(entityType, 40),
                EntityId = entityId,
                DepartmentId = departmentId,
                EmployeeId = employeeId,
                PositionId = positionId,
                Summary = Truncate(summary, 500),
                PreviousValue = previousValue,
                NewValue = newValue,
                EffectiveDate = effectiveDate?.Date,
                Now = DateTimeOffset.UtcNow,
                ActorUserId = actorUserId,
                ActorName = Truncate(actorName, 200)
            }, tx, cancellationToken: ct));
    }

    private const string ActivityWhereSql = @"
WHERE a.organization_id = @OrganizationId
  AND (@DepartmentId IS NULL OR a.department_id = @DepartmentId)
  AND (@EmployeeId IS NULL OR a.employee_id = @EmployeeId)
  AND (@PositionId IS NULL OR a.position_id = @PositionId)
  AND (@EntityId IS NULL OR a.entity_id = @EntityId)
  AND (@EntityType IS NULL OR a.entity_type = @EntityType)
  AND (@ChangeType IS NULL OR a.change_type = @ChangeType)";

    private static object ActivityParameters(Guid organizationId, OrgActivityQuery query, int? offset = null, int? pageSize = null) => new
    {
        OrganizationId = organizationId,
        query.DepartmentId,
        query.EmployeeId,
        query.PositionId,
        query.EntityId,
        EntityType = Trim(query.EntityType),
        ChangeType = Trim(query.ChangeType),
        Offset = offset ?? 0,
        PageSize = pageSize ?? 50
    };

    public async Task<IReadOnlyList<OrgActivityDto>> ListActivityAsync(Guid organizationId, OrgActivityQuery query, CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var sql = $@"
SELECT a.id AS Id, a.change_type AS Action, a.entity_type AS EntityType, a.entity_id AS EntityId,
       CASE a.entity_type
            WHEN N'Department' THEN (SELECT TOP 1 x.name FROM dbo.nh_hr_departments x WHERE x.organization_id = a.organization_id AND x.id = a.entity_id)
            WHEN N'Position' THEN (SELECT TOP 1 x.position_name FROM dbo.nh_hr_organizational_positions x WHERE x.organization_id = a.organization_id AND x.id = a.entity_id)
            WHEN N'Location' THEN (SELECT TOP 1 x.name FROM dbo.nh_hr_work_locations x WHERE x.organization_id = a.organization_id AND x.id = a.entity_id)
            WHEN N'Employee' THEN (SELECT TOP 1 {EmployeeNameSql} FROM dbo.nh_hr_employees e WHERE e.organization_id = a.organization_id AND e.id = a.entity_id)
            ELSE NULL END AS EntityName,
       a.summary AS Description, a.department_id AS DepartmentId, a.employee_id AS EmployeeId, a.position_id AS PositionId,
       a.effective_date AS EffectiveDate, a.created_at AS CreatedAt, a.created_by_name AS ActorName
FROM dbo.nh_hr_organization_activity a
{ActivityWhereSql}
ORDER BY a.created_at DESC
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";

        using var connection = OpenConnection();
        var rows = await connection.QueryAsync<OrgActivityDto>(new CommandDefinition(sql,
            ActivityParameters(organizationId, query, (page - 1) * pageSize, pageSize), cancellationToken: cancellationToken));
        return rows.ToList();
    }

    public async Task<int> CountActivityAsync(Guid organizationId, OrgActivityQuery query, CancellationToken cancellationToken = default)
    {
        var sql = "SELECT COUNT(1) FROM dbo.nh_hr_organization_activity a " + ActivityWhereSql + ";";
        using var connection = OpenConnection();
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql,
            ActivityParameters(organizationId, query), cancellationToken: cancellationToken));
    }

    // -------------------------------------------------------------------------
    // Lookups
    // -------------------------------------------------------------------------

    public async Task<bool> DepartmentExistsAsync(Guid organizationId, Guid id, CancellationToken cancellationToken = default) =>
        await ExistsAsync("dbo.nh_hr_departments", "archived_at IS NULL", organizationId, id, cancellationToken);

    public async Task<bool> EmployeeExistsAsync(Guid organizationId, Guid id, CancellationToken cancellationToken = default) =>
        await ExistsAsync("dbo.nh_hr_employees", "archived_at IS NULL", organizationId, id, cancellationToken);

    public async Task<bool> PositionExistsAsync(Guid organizationId, Guid id, CancellationToken cancellationToken = default) =>
        await ExistsAsync("dbo.nh_hr_organizational_positions", "archived_at IS NULL", organizationId, id, cancellationToken);

    private async Task<bool> ExistsAsync(string table, string extraWhere, Guid organizationId, Guid id, CancellationToken cancellationToken)
    {
        using var connection = OpenConnection();
        var count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(1) FROM {table} WHERE organization_id = @OrganizationId AND id = @Id AND {extraWhere};",
            new { OrganizationId = organizationId, Id = id }, cancellationToken: cancellationToken));
        return count > 0;
    }

    // -------------------------------------------------------------------------
    // Internal loaders
    // -------------------------------------------------------------------------

    private sealed class DeptRow : OrgDepartmentDetailDto
    {
        public int FilledHeadcount { get; set; }

        public OrgDepartmentDetailDto ToDetail() => new()
        {
            Id = Id,
            Name = Name,
            Code = Code,
            Description = Description,
            ParentDepartmentId = ParentDepartmentId,
            ParentDepartmentName = ParentDepartmentName,
            HeadEmployeeId = HeadEmployeeId,
            HeadName = HeadName,
            HeadPhotoUrl = HeadPhotoUrl,
            HeadJobTitle = HeadJobTitle,
            CostCenter = CostCenter,
            Color = Color,
            Icon = Icon,
            DisplayOrder = DisplayOrder,
            IsActive = IsActive,
            EmployeeCount = EmployeeCount,
            DirectEmployeeCount = DirectEmployeeCount,
            TeamCount = TeamCount,
            PositionCount = PositionCount,
            OpenPositionCount = OpenPositionCount,
            BudgetedHeadcount = BudgetedHeadcount,
            UtilizationPercent = UtilizationPercent,
            Members = Members,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt
        };
    }

    private sealed class MemberRow
    {
        public Guid DepartmentId { get; set; }
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string? JobTitle { get; set; }
        public string? PhotoUrl { get; set; }
    }

    /// <summary>Loads departments with self aggregates, then rolls employees/positions up the hierarchy in memory.</summary>
    private async Task<List<DeptRow>> LoadDepartmentRowsAsync(IDbConnection connection, Guid organizationId, bool includeArchived, CancellationToken ct)
    {
        var rows = (await connection.QueryAsync<DeptRow>(new CommandDefinition(DepartmentSelectSql,
            new { OrganizationId = organizationId, IncludeArchived = includeArchived ? 1 : 0 }, cancellationToken: ct))).ToList();
        if (rows.Count == 0) return rows;

        var members = (await connection.QueryAsync<MemberRow>(new CommandDefinition($@"
SELECT x.DepartmentId, x.Id, x.Name, x.JobTitle, x.PhotoUrl
FROM (
    SELECT e.department_id AS DepartmentId, e.id AS Id, {EmployeeNameSql} AS Name, jt.name AS JobTitle,
           e.profile_picture_url AS PhotoUrl,
           ROW_NUMBER() OVER (PARTITION BY e.department_id
                              ORDER BY CASE WHEN e.id = d.head_employee_id THEN 0 ELSE 1 END, e.hire_date, e.employee_number) AS rn
    FROM dbo.nh_hr_employees e
    JOIN dbo.nh_hr_departments d ON d.id = e.department_id AND d.organization_id = e.organization_id
    LEFT JOIN dbo.nh_hr_job_titles jt ON jt.id = e.job_title_id AND jt.organization_id = e.organization_id
    WHERE e.organization_id = @OrganizationId AND {ActiveEmployeeSql}
) x
WHERE x.rn <= @Take;",
            new { OrganizationId = organizationId, Take = ChartMemberCount }, cancellationToken: ct)))
            .GroupBy(m => m.DepartmentId)
            .ToDictionary(g => g.Key, g => g.Select(m => new OrgChartAvatarDto { Id = m.Id, Name = m.Name, JobTitle = m.JobTitle, PhotoUrl = m.PhotoUrl }).ToList());

        var byId = rows.ToDictionary(r => r.Id);
        var children = rows
            .Where(r => r.ParentDepartmentId.HasValue && r.ParentDepartmentId != r.Id && byId.ContainsKey(r.ParentDepartmentId.Value))
            .GroupBy(r => r.ParentDepartmentId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Self aggregates, captured before roll-up overwrites the public fields.
        var self = rows.ToDictionary(r => r.Id, r => (Emp: r.DirectEmployeeCount, Pos: r.PositionCount, Open: r.OpenPositionCount, Budget: r.BudgetedHeadcount, Filled: r.FilledHeadcount));
        var memo = new Dictionary<Guid, (int Emp, int Pos, int Open, int? Budget, int Filled)>();

        (int Emp, int Pos, int Open, int? Budget, int Filled) Roll(Guid id, HashSet<Guid> path)
        {
            if (memo.TryGetValue(id, out var cached)) return cached;
            var s = self[id];
            var total = (Emp: s.Emp, Pos: s.Pos, Open: s.Open, Budget: s.Budget, Filled: s.Filled);
            if (path.Add(id))
            {
                if (children.TryGetValue(id, out var kids))
                {
                    foreach (var kid in kids)
                    {
                        if (path.Contains(kid.Id)) continue;
                        var c = Roll(kid.Id, path);
                        total.Emp += c.Emp;
                        total.Pos += c.Pos;
                        total.Open += c.Open;
                        total.Filled += c.Filled;
                        if (c.Budget.HasValue) total.Budget = (total.Budget ?? 0) + c.Budget.Value;
                    }
                }
                path.Remove(id);
            }
            memo[id] = total;
            return total;
        }

        foreach (var row in rows)
        {
            var rolled = Roll(row.Id, new HashSet<Guid>());
            row.EmployeeCount = rolled.Emp;
            row.PositionCount = rolled.Pos;
            row.OpenPositionCount = rolled.Open;
            row.BudgetedHeadcount = rolled.Budget;
            row.UtilizationPercent = rolled.Budget is > 0 ? Math.Round(rolled.Filled * 100.0 / rolled.Budget.Value, 1) : null;
            row.TeamCount = children.TryGetValue(row.Id, out var kids) ? kids.Count : 0;
            row.Members = members.TryGetValue(row.Id, out var m) ? m : [];
        }

        return rows;
    }

    private sealed class PersonRow
    {
        public Guid EmployeeId { get; set; }
        public string Name { get; set; } = "";
        public string? JobTitle { get; set; }
        public Guid? DepartmentId { get; set; }
        public string? DepartmentName { get; set; }
        public string? PhotoUrl { get; set; }
        public Guid? ManagerId { get; set; }
        public string EmploymentStatus { get; set; } = "";
    }

    private static async Task<List<PersonRow>> LoadPeopleAsync(IDbConnection connection, Guid organizationId, CancellationToken ct)
    {
        var sql = $@"
SELECT e.id AS EmployeeId, {EmployeeNameSql} AS Name, jt.name AS JobTitle,
       e.department_id AS DepartmentId, dep.name AS DepartmentName,
       e.profile_picture_url AS PhotoUrl, e.manager_id AS ManagerId, e.employment_status AS EmploymentStatus
FROM dbo.nh_hr_employees e
LEFT JOIN dbo.nh_hr_job_titles jt ON jt.id = e.job_title_id AND jt.organization_id = e.organization_id
LEFT JOIN dbo.nh_hr_departments dep ON dep.id = e.department_id AND dep.organization_id = e.organization_id
WHERE e.organization_id = @OrganizationId AND {ActiveEmployeeSql}
ORDER BY e.last_name, e.first_name;";

        return (await connection.QueryAsync<PersonRow>(new CommandDefinition(sql,
            new { OrganizationId = organizationId }, cancellationToken: ct))).ToList();
    }

    /// <summary>In-memory manager hierarchy over the active workforce.</summary>
    private sealed class PeopleTree
    {
        public List<PersonRow> Roots { get; private init; } = [];
        public Dictionary<Guid, List<PersonRow>> Children { get; private init; } = new();

        public static PeopleTree Build(List<PersonRow> people)
        {
            var ids = people.Select(p => p.EmployeeId).ToHashSet();
            var tree = new PeopleTree();
            foreach (var p in people)
            {
                if (p.ManagerId.HasValue && p.ManagerId.Value != p.EmployeeId && ids.Contains(p.ManagerId.Value))
                {
                    if (!tree.Children.TryGetValue(p.ManagerId.Value, out var list))
                        tree.Children[p.ManagerId.Value] = list = [];
                    list.Add(p);
                }
                else
                {
                    tree.Roots.Add(p);
                }
            }
            return tree;
        }

        public int TotalReports(Guid id, Dictionary<Guid, int> memo)
        {
            if (memo.TryGetValue(id, out var cached)) return cached;
            var visited = new HashSet<Guid> { id };
            var stack = new Stack<Guid>();
            stack.Push(id);
            var count = 0;
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!Children.TryGetValue(current, out var kids)) continue;
                foreach (var kid in kids)
                {
                    if (!visited.Add(kid.EmployeeId)) continue;
                    count++;
                    stack.Push(kid.EmployeeId);
                }
            }
            memo[id] = count;
            return count;
        }

        public int MaxDepth()
        {
            var max = 0;
            var visited = new HashSet<Guid>();
            var queue = new Queue<(Guid Id, int Depth)>();
            foreach (var root in Roots)
            {
                if (visited.Add(root.EmployeeId)) queue.Enqueue((root.EmployeeId, 1));
            }
            while (queue.Count > 0)
            {
                var (id, depth) = queue.Dequeue();
                max = Math.Max(max, depth);
                if (!Children.TryGetValue(id, out var kids)) continue;
                foreach (var kid in kids)
                {
                    if (visited.Add(kid.EmployeeId)) queue.Enqueue((kid.EmployeeId, depth + 1));
                }
            }
            return max;
        }
    }

    private sealed class OccupantRow
    {
        public Guid PositionId { get; set; }
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string? JobTitle { get; set; }
        public string? PhotoUrl { get; set; }
    }

    private static async Task FillOccupantsAsync(IDbConnection connection, Guid organizationId, IReadOnlyList<OrgPositionListItemDto> rows, CancellationToken ct)
    {
        foreach (var row in rows) row.OpenCount = Math.Max(0, row.Headcount - row.FilledCount);
        if (rows.Count == 0) return;

        var occupants = (await connection.QueryAsync<OccupantRow>(new CommandDefinition($@"
SELECT a.position_id AS PositionId, e.id AS Id, {EmployeeNameSql} AS Name, jt.name AS JobTitle, e.profile_picture_url AS PhotoUrl
FROM dbo.nh_hr_employee_position_assignments a
JOIN dbo.nh_hr_employees e ON e.id = a.employee_id AND e.organization_id = a.organization_id
LEFT JOIN dbo.nh_hr_job_titles jt ON jt.id = e.job_title_id AND jt.organization_id = e.organization_id
WHERE a.organization_id = @OrganizationId AND a.position_id IN @Ids AND a.end_date IS NULL AND a.status = N'Active'
  AND e.archived_at IS NULL
ORDER BY a.effective_date, e.last_name;",
            new { OrganizationId = organizationId, Ids = rows.Select(r => r.Id).ToArray() }, cancellationToken: ct)))
            .GroupBy(o => o.PositionId)
            .ToDictionary(g => g.Key, g => g.Select(o => new OrgChartAvatarDto { Id = o.Id, Name = o.Name, JobTitle = o.JobTitle, PhotoUrl = o.PhotoUrl }).ToList());

        foreach (var row in rows)
            row.Occupants = occupants.TryGetValue(row.Id, out var list) ? list : [];
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private IDbConnection OpenConnection()
    {
        var connection = _connectionFactory.CreateConnection();
        connection.Open();
        return connection;
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int max) =>
        value == null ? null : (value.Length <= max ? value : value[..max]);

    private static bool IsDuplicateKey(SqlException ex) => ex.Number is 2627 or 2601;

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    private static string ResolveSchemaScriptPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "sql", SchemaScriptFileName),
            Path.Combine(Directory.GetCurrentDirectory(), "sql", SchemaScriptFileName)
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            $"Core HR organization schema script not found. Expected sql/{SchemaScriptFileName}.");
    }
}
