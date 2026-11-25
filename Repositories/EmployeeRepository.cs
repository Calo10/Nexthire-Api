using System.Data;
using System.Globalization;
using Dapper;
using nexthire_api.Data;
using nexthire_api.DTOs;
using nexthire_api.DTOs.CoreHr;

namespace nexthire_api.Repositories;

/// <summary>
/// Dapper/SQL Server implementation of Core HR employee persistence.
/// All queries are parameterized and scoped by organization_id.
/// </summary>
public class EmployeeRepository : IEmployeeRepository
{
    private const string SchemaScriptFileName = "009-core-hr-employees-schema.sql";
    private const string EmployeeNumberPrefix = "EMP-";

    private static readonly (string Code, string Name, int Sort)[] DefaultEmploymentTypes =
    [
        ("FullTime", "Full-time", 1),
        ("PartTime", "Part-time", 2),
        ("Contractor", "Contractor", 3),
        ("Temporary", "Temporary", 4),
        ("Intern", "Intern", 5)
    ];

    private static readonly Dictionary<string, string> SortColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = "e.last_name {dir}, e.first_name {dir}",
        ["employeeNumber"] = "e.employee_number {dir}",
        ["hireDate"] = "e.hire_date {dir}",
        ["status"] = "e.employment_status {dir}",
        ["department"] = "d.name {dir}",
        ["jobTitle"] = "jt.name {dir}"
    };

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<EmployeeRepository> _logger;

    public EmployeeRepository(IDbConnectionFactory connectionFactory, ILogger<EmployeeRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // Schema / defaults / demo data
    // -------------------------------------------------------------------------

    public async Task EnsureSchemaAsync()
    {
        var path = ResolveSchemaScriptPath();
        var sql = await File.ReadAllTextAsync(path);
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        await connection.ExecuteAsync(new CommandDefinition(sql));
    }

    public async Task EnsureOrgDefaultsAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        await EnsureOrgDefaultsCoreAsync(connection, null, organizationId, cancellationToken);
    }

    public async Task SeedDemoEmployeesIfEmptyAsync(
        Guid organizationId,
        Guid? actorUserId,
        bool isDevelopment,
        CancellationToken cancellationToken = default)
    {
        if (!isDevelopment) return;

        using var connection = OpenConnection();

        var existing = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM dbo.nh_hr_employees WHERE organization_id = @OrganizationId;",
            new { OrganizationId = organizationId },
            cancellationToken: cancellationToken));
        if (existing > 0) return;

        using var tx = connection.BeginTransaction();
        try
        {
            await EnsureOrgDefaultsCoreAsync(connection, tx, organizationId, cancellationToken);

            var now = DateTimeOffset.UtcNow;
            var departments = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in DemoDepartments)
                departments[name] = await UpsertNamedLookupAsync(connection, tx, "nh_hr_departments", organizationId, name, null, actorUserId, cancellationToken);

            var titles = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in DemoJobTitles.Select(t => t.Title).Distinct(StringComparer.OrdinalIgnoreCase))
                titles[name] = await UpsertNamedLookupAsync(connection, tx, "nh_hr_job_titles", organizationId, name, null, actorUserId, cancellationToken);

            var locations = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (var loc in DemoLocations)
            {
                locations[loc.Name] = await UpsertLocationCoreAsync(connection, tx, organizationId,
                    new CreateHrLookupRequest { Name = loc.Name, City = loc.City, State = loc.State, Country = loc.Country },
                    actorUserId, cancellationToken);
            }

            var typeRows = (await connection.QueryAsync<(Guid Id, string Code)>(new CommandDefinition(
                "SELECT id, code FROM dbo.nh_hr_employment_types WHERE organization_id = @OrganizationId;",
                new { OrganizationId = organizationId }, tx, cancellationToken: cancellationToken)))
                .ToDictionary(r => r.Code, r => r.Id, StringComparer.OrdinalIgnoreCase);

            var today = DateTime.UtcNow.Date;
            var managerByDept = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            var seq = 0;

            foreach (var demo in DemoEmployees)
            {
                seq++;
                var id = Guid.NewGuid();
                var titleName = DemoJobTitles.First(t => t.Dept.Equals(demo.Dept, StringComparison.OrdinalIgnoreCase) && t.Level == demo.TitleLevel).Title;
                Guid? managerId = null;
                if (demo.IsManager)
                    managerByDept[demo.Dept] = id;
                else if (managerByDept.TryGetValue(demo.Dept, out var mid))
                    managerId = mid;

                var hireDate = today.AddDays(-demo.HiredDaysAgo);
                DateTime? terminationDate = demo.Status == EmploymentStatuses.Terminated
                    ? today.AddDays(-Math.Max(5, demo.HiredDaysAgo / 4))
                    : null;
                if (terminationDate < hireDate) terminationDate = hireDate.AddDays(30);

                var request = new CreateEmployeeRequest
                {
                    FirstName = demo.First,
                    LastName = demo.Last,
                    PreferredName = demo.Preferred,
                    WorkEmail = $"{Slug(demo.First)}.{Slug(demo.Last)}@demo.nexthire.example",
                    WorkPhone = $"+52 55 5550 {1000 + seq:D4}",
                    PersonalEmail = $"{Slug(demo.First)}.{Slug(demo.Last)}@example.org",
                    Nationality = "Mexican",
                    City = "Mexico City",
                    Country = "Mexico",
                    JobTitleId = titles[titleName],
                    DepartmentId = departments[demo.Dept],
                    ManagerId = managerId,
                    EmploymentTypeId = typeRows.TryGetValue(demo.TypeCode, out var tid) ? tid : null,
                    EmploymentStatus = demo.Status,
                    HireDate = hireDate,
                    WorkLocationId = locations[demo.Location],
                    WorkModality = demo.Location == "Remote" ? "Remote" : (seq % 5 == 0 ? "Hybrid" : "OnSite")
                };

                await InsertEmployeeCoreAsync(connection, tx, organizationId, id, request,
                    $"{EmployeeNumberPrefix}{seq:D4}", actorUserId, now, cancellationToken);

                if (terminationDate.HasValue)
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        @"UPDATE dbo.nh_hr_employees
                          SET termination_date = @TerminationDate, termination_reason = @Reason
                          WHERE organization_id = @OrganizationId AND id = @Id;",
                        new { OrganizationId = organizationId, Id = id, TerminationDate = terminationDate.Value, Reason = "Demo data: voluntary resignation" },
                        tx, cancellationToken: cancellationToken));
                }

                await InsertHistoryCoreAsync(connection, tx, organizationId, id, "Hire", request, actorUserId,
                    "Demo data", new DateTimeOffset(hireDate, TimeSpan.Zero), cancellationToken);
                await InsertActivityCoreAsync(connection, tx, organizationId, id, "Created",
                    "Employee record created (demo data)", null, actorUserId, "System", cancellationToken);
            }

            tx.Commit();
            _logger.LogInformation("Seeded {Count} demo employees for organization {OrganizationId}.", DemoEmployees.Length, organizationId);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // -------------------------------------------------------------------------
    // Employees: read
    // -------------------------------------------------------------------------

    public async Task<PagedResult<EmployeeListItemDto>> ListAsync(
        Guid organizationId,
        EmployeeListQuery query,
        CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 500);
        var offset = (page - 1) * pageSize;

        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var searchLike = search == null ? null : "%" + EscapeLike(search) + "%";
        var status = string.IsNullOrWhiteSpace(query.EmploymentStatus) ? null : query.EmploymentStatus.Trim();

        var orderBy = BuildOrderBy(query.Sort, query.Dir);

        var where = @"
WHERE e.organization_id = @OrganizationId
  AND (@IncludeArchived = 1 OR e.archived_at IS NULL)
  AND (@DepartmentId IS NULL OR e.department_id = @DepartmentId)
  AND (@JobTitleId IS NULL OR e.job_title_id = @JobTitleId)
  AND (@EmploymentStatus IS NULL OR e.employment_status = @EmploymentStatus)
  AND (@EmploymentTypeId IS NULL OR e.employment_type_id = @EmploymentTypeId)
  AND (@WorkLocationId IS NULL OR e.work_location_id = @WorkLocationId)
  AND (@ManagerId IS NULL OR e.manager_id = @ManagerId)
  AND (@HireFrom IS NULL OR e.hire_date >= @HireFrom)
  AND (@HireTo IS NULL OR e.hire_date <= @HireTo)
  AND (
        @SearchLike IS NULL
        OR e.first_name LIKE @SearchLike ESCAPE '\'
        OR e.last_name LIKE @SearchLike ESCAPE '\'
        OR ISNULL(e.preferred_name, '') LIKE @SearchLike ESCAPE '\'
        OR (e.first_name + ' ' + e.last_name) LIKE @SearchLike ESCAPE '\'
        OR ISNULL(e.work_email, '') LIKE @SearchLike ESCAPE '\'
        OR e.employee_number LIKE @SearchLike ESCAPE '\'
      )";

        const string joins = @"
FROM dbo.nh_hr_employees e
LEFT JOIN dbo.nh_hr_departments d ON d.id = e.department_id AND d.organization_id = e.organization_id
LEFT JOIN dbo.nh_hr_job_titles jt ON jt.id = e.job_title_id AND jt.organization_id = e.organization_id
LEFT JOIN dbo.nh_hr_work_locations wl ON wl.id = e.work_location_id AND wl.organization_id = e.organization_id
LEFT JOIN dbo.nh_hr_employment_types et ON et.id = e.employment_type_id AND et.organization_id = e.organization_id
LEFT JOIN dbo.nh_hr_employees m ON m.id = e.manager_id AND m.organization_id = e.organization_id";

        var countSql = "SELECT COUNT(1) " + joins + where + ";";
        var pageSql = SelectListColumns + joins + where + $@"
ORDER BY {orderBy}, e.id
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";

        var args = new
        {
            OrganizationId = organizationId,
            IncludeArchived = query.IncludeArchived ? 1 : 0,
            query.DepartmentId,
            query.JobTitleId,
            EmploymentStatus = status,
            query.EmploymentTypeId,
            query.WorkLocationId,
            query.ManagerId,
            HireFrom = query.HireFrom?.Date,
            HireTo = query.HireTo?.Date,
            SearchLike = searchLike,
            Offset = offset,
            PageSize = pageSize
        };

        using var connection = OpenConnection();
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(countSql, args, cancellationToken: cancellationToken));
        var rows = (await connection.QueryAsync<EmployeeListItemDto>(new CommandDefinition(pageSql, args, cancellationToken: cancellationToken))).ToList();
        foreach (var row in rows) row.DisplayName = ComputeDisplayName(row.FirstName, row.LastName, row.PreferredName);

        return new PagedResult<EmployeeListItemDto>
        {
            Items = rows,
            Page = page,
            PageSize = pageSize,
            Total = total
        };
    }

    public async Task<EmployeeDetailDto?> GetByIdAsync(Guid organizationId, Guid employeeId, CancellationToken cancellationToken = default)
    {
        const string sql = SelectDetailColumns + @"
FROM dbo.nh_hr_employees e
LEFT JOIN dbo.nh_hr_departments d ON d.id = e.department_id AND d.organization_id = e.organization_id
LEFT JOIN dbo.nh_hr_job_titles jt ON jt.id = e.job_title_id AND jt.organization_id = e.organization_id
LEFT JOIN dbo.nh_hr_work_locations wl ON wl.id = e.work_location_id AND wl.organization_id = e.organization_id
LEFT JOIN dbo.nh_hr_employment_types et ON et.id = e.employment_type_id AND et.organization_id = e.organization_id
LEFT JOIN dbo.nh_hr_employees m ON m.id = e.manager_id AND m.organization_id = e.organization_id
WHERE e.organization_id = @OrganizationId AND e.id = @EmployeeId;";

        using var connection = OpenConnection();
        var employee = await connection.QuerySingleOrDefaultAsync<EmployeeDetailDto>(new CommandDefinition(
            sql, new { OrganizationId = organizationId, EmployeeId = employeeId }, cancellationToken: cancellationToken));
        if (employee == null) return null;

        employee.DisplayName = ComputeDisplayName(employee.FirstName, employee.LastName, employee.PreferredName);
        employee.IsArchived = employee.ArchivedAt.HasValue;
        employee.TenureYears = ComputeTenureYears(employee.HireDate, employee.TerminationDate);
        employee.EmergencyContacts = await ListEmergencyContactsCoreAsync(connection, organizationId, employeeId, cancellationToken);

        var horizon = DateTime.UtcNow.Date.AddDays(60);
        var docs = await connection.QueryAsync<EmployeeDocumentDto>(new CommandDefinition(
            DocumentSelect + @"
WHERE organization_id = @OrganizationId AND employee_id = @EmployeeId
  AND archived_at IS NULL AND expires_at IS NOT NULL AND expires_at <= @Horizon
ORDER BY expires_at ASC;",
            new { OrganizationId = organizationId, EmployeeId = employeeId, Horizon = horizon },
            cancellationToken: cancellationToken));
        employee.UpcomingDocumentExpirations = docs.ToList();

        return employee;
    }

    public async Task<Guid?> FindActiveByCandidateIdAsync(Guid organizationId, Guid candidateId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT TOP 1 id
FROM dbo.nh_hr_employees
WHERE organization_id = @OrganizationId AND candidate_id = @CandidateId AND archived_at IS NULL;";

        using var connection = OpenConnection();
        return await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            sql, new { OrganizationId = organizationId, CandidateId = candidateId }, cancellationToken: cancellationToken));
    }

    public async Task<bool> EmployeeNumberExistsAsync(Guid organizationId, string employeeNumber, Guid? excludeId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT CASE WHEN EXISTS (
    SELECT 1 FROM dbo.nh_hr_employees
    WHERE organization_id = @OrganizationId
      AND employee_number = @EmployeeNumber
      AND (@ExcludeId IS NULL OR id <> @ExcludeId)
) THEN 1 ELSE 0 END;";

        using var connection = OpenConnection();
        var exists = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            sql, new { OrganizationId = organizationId, EmployeeNumber = employeeNumber.Trim(), ExcludeId = excludeId }, cancellationToken: cancellationToken));
        return exists == 1;
    }

    public async Task<string> NextEmployeeNumberAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT MAX(TRY_CAST(SUBSTRING(employee_number, 5, 40) AS int))
FROM dbo.nh_hr_employees
WHERE organization_id = @OrganizationId AND employee_number LIKE 'EMP-%';";

        using var connection = OpenConnection();
        var max = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            sql, new { OrganizationId = organizationId }, cancellationToken: cancellationToken));
        var next = (max ?? 0) + 1;
        return EmployeeNumberPrefix + next.ToString("D4", CultureInfo.InvariantCulture);
    }

    public async Task<bool> WouldCreateManagerCycleAsync(Guid organizationId, Guid employeeId, Guid managerId, CancellationToken cancellationToken = default)
    {
        if (managerId == employeeId) return true;

        const string sql = @"
SELECT manager_id FROM dbo.nh_hr_employees
WHERE organization_id = @OrganizationId AND id = @Id;";

        using var connection = OpenConnection();
        var visited = new HashSet<Guid>();
        Guid? current = managerId;
        while (current.HasValue)
        {
            if (current.Value == employeeId) return true;
            if (!visited.Add(current.Value)) return false; // pre-existing cycle not involving employeeId
            current = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
                sql, new { OrganizationId = organizationId, Id = current.Value }, cancellationToken: cancellationToken));
        }
        return false;
    }

    // -------------------------------------------------------------------------
    // Employees: write
    // -------------------------------------------------------------------------

    public async Task<Guid> InsertAsync(
        Guid organizationId,
        Guid employeeId,
        CreateEmployeeRequest request,
        string employeeNumber,
        Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        await InsertEmployeeCoreAsync(connection, null, organizationId, employeeId, request, employeeNumber, actorUserId, DateTimeOffset.UtcNow, cancellationToken);
        return employeeId;
    }

    public async Task<bool> UpdateAsync(
        Guid organizationId,
        Guid employeeId,
        UpdateEmployeeRequest request,
        Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
UPDATE dbo.nh_hr_employees SET
    employee_number     = COALESCE(NULLIF(LTRIM(RTRIM(@EmployeeNumber)), ''), employee_number),
    first_name          = @FirstName,
    middle_name         = @MiddleName,
    last_name           = @LastName,
    preferred_name      = @PreferredName,
    profile_picture_url = @ProfilePictureUrl,
    date_of_birth       = @DateOfBirth,
    nationality         = @Nationality,
    personal_email      = @PersonalEmail,
    personal_phone      = @PersonalPhone,
    address_line1       = @AddressLine1,
    address_line2       = @AddressLine2,
    city                = @City,
    state               = @State,
    country             = @Country,
    postal_code         = @PostalCode,
    work_email          = @WorkEmail,
    work_phone          = @WorkPhone,
    job_title_id        = @JobTitleId,
    department_id       = @DepartmentId,
    manager_id          = @ManagerId,
    employment_type_id  = @EmploymentTypeId,
    employment_status   = COALESCE(@EmploymentStatus, employment_status),
    hire_date           = @HireDate,
    work_location_id    = @WorkLocationId,
    work_modality       = @WorkModality,
    probation_end_date  = @ProbationEndDate,
    contract_start_date = @ContractStartDate,
    contract_end_date   = @ContractEndDate,
    updated_at          = @Now,
    updated_by          = @ActorUserId
WHERE organization_id = @OrganizationId AND id = @EmployeeId AND archived_at IS NULL;";

        using var connection = OpenConnection();
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            sql, BuildEmployeeParameters(organizationId, employeeId, request, request.EmployeeNumber, actorUserId, DateTimeOffset.UtcNow),
            cancellationToken: cancellationToken));
        return rows > 0;
    }

    public async Task<bool> ChangeStatusAsync(
        Guid organizationId,
        Guid employeeId,
        ChangeEmployeeStatusRequest request,
        Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
UPDATE dbo.nh_hr_employees SET
    employment_status  = @Status,
    termination_date   = CASE WHEN @Status = 'Terminated' THEN COALESCE(@TerminationDate, CAST(SYSUTCDATETIME() AS date)) ELSE NULL END,
    termination_reason = CASE WHEN @Status = 'Terminated' THEN @TerminationReason ELSE NULL END,
    updated_at         = @Now,
    updated_by         = @ActorUserId
WHERE organization_id = @OrganizationId AND id = @EmployeeId AND archived_at IS NULL;";

        using var connection = OpenConnection();
        var rows = await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            OrganizationId = organizationId,
            EmployeeId = employeeId,
            Status = request.Status.Trim(),
            TerminationDate = request.TerminationDate?.Date,
            TerminationReason = Trim(request.TerminationReason),
            Now = DateTimeOffset.UtcNow,
            ActorUserId = actorUserId
        }, cancellationToken: cancellationToken));
        return rows > 0;
    }

    public async Task<bool> ArchiveAsync(Guid organizationId, Guid employeeId, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
UPDATE dbo.nh_hr_employees SET
    archived_at = @Now, archived_by = @ActorUserId, updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND id = @EmployeeId AND archived_at IS NULL;";

        using var connection = OpenConnection();
        var rows = await connection.ExecuteAsync(new CommandDefinition(sql,
            new { OrganizationId = organizationId, EmployeeId = employeeId, Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId },
            cancellationToken: cancellationToken));
        return rows > 0;
    }

    public async Task<bool> RestoreAsync(Guid organizationId, Guid employeeId, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
UPDATE dbo.nh_hr_employees SET
    archived_at = NULL, archived_by = NULL, updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND id = @EmployeeId AND archived_at IS NOT NULL;";

        using var connection = OpenConnection();
        var rows = await connection.ExecuteAsync(new CommandDefinition(sql,
            new { OrganizationId = organizationId, EmployeeId = employeeId, Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId },
            cancellationToken: cancellationToken));
        return rows > 0;
    }

    // -------------------------------------------------------------------------
    // Emergency contacts
    // -------------------------------------------------------------------------

    public async Task ReplaceEmergencyContactsAsync(
        Guid organizationId,
        Guid employeeId,
        IReadOnlyList<EmergencyContactDto> contacts,
        CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.nh_hr_employee_emergency_contacts WHERE organization_id = @OrganizationId AND employee_id = @EmployeeId;",
                new { OrganizationId = organizationId, EmployeeId = employeeId }, tx, cancellationToken: cancellationToken));

            const string insert = @"
INSERT INTO dbo.nh_hr_employee_emergency_contacts
    (id, organization_id, employee_id, full_name, relationship, phone, email, is_primary, created_at)
VALUES
    (@Id, @OrganizationId, @EmployeeId, @FullName, @Relationship, @Phone, @Email, @IsPrimary, @Now);";

            var now = DateTimeOffset.UtcNow;
            foreach (var c in contacts.Where(c => !string.IsNullOrWhiteSpace(c.FullName)))
            {
                await connection.ExecuteAsync(new CommandDefinition(insert, new
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = organizationId,
                    EmployeeId = employeeId,
                    FullName = c.FullName.Trim(),
                    Relationship = Trim(c.Relationship),
                    Phone = Trim(c.Phone),
                    Email = Trim(c.Email),
                    c.IsPrimary,
                    Now = now
                }, tx, cancellationToken: cancellationToken));
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<IReadOnlyList<EmergencyContactDto>> ListEmergencyContactsAsync(Guid organizationId, Guid employeeId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        return await ListEmergencyContactsCoreAsync(connection, organizationId, employeeId, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Activity / history
    // -------------------------------------------------------------------------

    public async Task AddActivityAsync(
        Guid organizationId,
        Guid employeeId,
        string activityType,
        string summary,
        string? detailsJson,
        Guid? actorUserId,
        string? actorName,
        CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        await InsertActivityCoreAsync(connection, null, organizationId, employeeId, activityType, summary, detailsJson, actorUserId, actorName, cancellationToken);
    }

    public async Task AddEmploymentHistoryAsync(
        Guid organizationId,
        Guid employeeId,
        string changeType,
        CreateEmployeeRequest snapshot,
        Guid? actorUserId,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        await InsertHistoryCoreAsync(connection, null, organizationId, employeeId, changeType, snapshot, actorUserId, notes, DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task<IReadOnlyList<EmployeeActivityDto>> ListActivityAsync(Guid organizationId, Guid employeeId, int take, CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT TOP (@Take)
    id AS Id, activity_type AS ActivityType, summary AS Summary, details_json AS DetailsJson,
    created_at AS CreatedAt, created_by_name AS CreatedByName
FROM dbo.nh_hr_employee_activity
WHERE organization_id = @OrganizationId AND employee_id = @EmployeeId
ORDER BY created_at DESC;";

        using var connection = OpenConnection();
        var rows = await connection.QueryAsync<EmployeeActivityDto>(new CommandDefinition(sql,
            new { OrganizationId = organizationId, EmployeeId = employeeId, Take = Math.Clamp(take, 1, 500) },
            cancellationToken: cancellationToken));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<EmploymentHistoryDto>> ListHistoryAsync(Guid organizationId, Guid employeeId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT
    h.id AS Id,
    h.effective_at AS EffectiveAt,
    h.change_type AS ChangeType,
    jt.name AS JobTitle,
    d.name AS Department,
    et.name AS EmploymentType,
    h.employment_status AS EmploymentStatus,
    wl.name AS Location,
    CASE WHEN m.id IS NULL THEN NULL
         ELSE COALESCE(NULLIF(LTRIM(RTRIM(m.preferred_name)), ''), m.first_name + ' ' + m.last_name) END AS ManagerName,
    h.notes AS Notes,
    (SELECT TOP 1 a.created_by_name FROM dbo.nh_hr_employee_activity a
      WHERE a.organization_id = h.organization_id AND a.employee_id = h.employee_id
        AND a.created_by = h.created_by AND a.created_by_name IS NOT NULL
      ORDER BY a.created_at DESC) AS CreatedByName
FROM dbo.nh_hr_employee_employment_history h
LEFT JOIN dbo.nh_hr_job_titles jt ON jt.id = h.job_title_id AND jt.organization_id = h.organization_id
LEFT JOIN dbo.nh_hr_departments d ON d.id = h.department_id AND d.organization_id = h.organization_id
LEFT JOIN dbo.nh_hr_employment_types et ON et.id = h.employment_type_id AND et.organization_id = h.organization_id
LEFT JOIN dbo.nh_hr_work_locations wl ON wl.id = h.work_location_id AND wl.organization_id = h.organization_id
LEFT JOIN dbo.nh_hr_employees m ON m.id = h.manager_id AND m.organization_id = h.organization_id
WHERE h.organization_id = @OrganizationId AND h.employee_id = @EmployeeId
ORDER BY h.effective_at DESC, h.created_at DESC;";

        using var connection = OpenConnection();
        var rows = await connection.QueryAsync<EmploymentHistoryDto>(new CommandDefinition(sql,
            new { OrganizationId = organizationId, EmployeeId = employeeId }, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    // -------------------------------------------------------------------------
    // Documents
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<EmployeeDocumentDto>> ListDocumentsAsync(Guid organizationId, Guid employeeId, bool includeArchived, CancellationToken cancellationToken = default)
    {
        var sql = DocumentSelect + @"
WHERE organization_id = @OrganizationId AND employee_id = @EmployeeId
  AND (@IncludeArchived = 1 OR archived_at IS NULL)
ORDER BY created_at DESC;";

        using var connection = OpenConnection();
        var rows = await connection.QueryAsync<EmployeeDocumentDto>(new CommandDefinition(sql,
            new { OrganizationId = organizationId, EmployeeId = employeeId, IncludeArchived = includeArchived ? 1 : 0 },
            cancellationToken: cancellationToken));
        return rows.ToList();
    }

    public async Task<EmployeeDocumentDto?> GetDocumentAsync(Guid organizationId, Guid documentRowId, CancellationToken cancellationToken = default)
    {
        var sql = DocumentSelect + " WHERE organization_id = @OrganizationId AND id = @Id;";

        using var connection = OpenConnection();
        return await connection.QuerySingleOrDefaultAsync<EmployeeDocumentDto>(new CommandDefinition(sql,
            new { OrganizationId = organizationId, Id = documentRowId }, cancellationToken: cancellationToken));
    }

    public async Task<Guid> InsertDocumentAsync(Guid organizationId, Guid employeeId, UpsertEmployeeDocumentRequest request, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
INSERT INTO dbo.nh_hr_employee_documents
    (id, organization_id, employee_id, category, name, document_id, external_url, status, expires_at, created_at, created_by)
VALUES
    (@Id, @OrganizationId, @EmployeeId, @Category, @Name, @DocumentId, @ExternalUrl, @Status, @ExpiresAt, @Now, @ActorUserId);";

        var id = Guid.NewGuid();
        using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id = id,
            OrganizationId = organizationId,
            EmployeeId = employeeId,
            Category = request.Category.Trim(),
            Name = request.Name.Trim(),
            request.DocumentId,
            ExternalUrl = Trim(request.ExternalUrl),
            Status = string.IsNullOrWhiteSpace(request.Status) ? "Active" : request.Status.Trim(),
            ExpiresAt = request.ExpiresAt?.Date,
            Now = DateTimeOffset.UtcNow,
            ActorUserId = actorUserId
        }, cancellationToken: cancellationToken));
        return id;
    }

    public async Task<bool> ArchiveDocumentAsync(Guid organizationId, Guid documentRowId, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
UPDATE dbo.nh_hr_employee_documents
SET archived_at = @Now, archived_by = @ActorUserId, status = 'Archived'
WHERE organization_id = @OrganizationId AND id = @Id AND archived_at IS NULL;";

        using var connection = OpenConnection();
        var rows = await connection.ExecuteAsync(new CommandDefinition(sql,
            new { OrganizationId = organizationId, Id = documentRowId, Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId },
            cancellationToken: cancellationToken));
        return rows > 0;
    }

    // -------------------------------------------------------------------------
    // Compensation
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<CompensationDto>> ListCompensationAsync(Guid organizationId, Guid employeeId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT id AS Id, amount AS Amount, currency AS Currency, pay_frequency AS PayFrequency,
       effective_date AS EffectiveDate, end_date AS EndDate, notes AS Notes
FROM dbo.nh_hr_employee_compensation
WHERE organization_id = @OrganizationId AND employee_id = @EmployeeId
ORDER BY effective_date DESC, created_at DESC;";

        using var connection = OpenConnection();
        var rows = await connection.QueryAsync<CompensationDto>(new CommandDefinition(sql,
            new { OrganizationId = organizationId, EmployeeId = employeeId }, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    public async Task<Guid> InsertCompensationAsync(Guid organizationId, Guid employeeId, UpsertCompensationRequest request, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        try
        {
            // Close any previous open-ended record that started before the new one.
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_employee_compensation
SET end_date = DATEADD(day, -1, @EffectiveDate)
WHERE organization_id = @OrganizationId AND employee_id = @EmployeeId
  AND end_date IS NULL AND effective_date < @EffectiveDate;",
                new { OrganizationId = organizationId, EmployeeId = employeeId, EffectiveDate = request.EffectiveDate.Date },
                tx, cancellationToken: cancellationToken));

            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_employee_compensation
    (id, organization_id, employee_id, amount, currency, pay_frequency, effective_date, end_date, notes, created_at, created_by)
VALUES
    (@Id, @OrganizationId, @EmployeeId, @Amount, @Currency, @PayFrequency, @EffectiveDate, @EndDate, @Notes, @Now, @ActorUserId);",
                new
                {
                    Id = id,
                    OrganizationId = organizationId,
                    EmployeeId = employeeId,
                    request.Amount,
                    Currency = string.IsNullOrWhiteSpace(request.Currency) ? "USD" : request.Currency.Trim().ToUpperInvariant(),
                    PayFrequency = string.IsNullOrWhiteSpace(request.PayFrequency) ? "Monthly" : request.PayFrequency.Trim(),
                    EffectiveDate = request.EffectiveDate.Date,
                    EndDate = request.EndDate?.Date,
                    Notes = Trim(request.Notes),
                    Now = DateTimeOffset.UtcNow,
                    ActorUserId = actorUserId
                }, tx, cancellationToken: cancellationToken));

            tx.Commit();
            return id;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // -------------------------------------------------------------------------
    // Summary
    // -------------------------------------------------------------------------

    public async Task<EmployeeSummaryDto> GetSummaryAsync(Guid organizationId, DateTime? from, DateTime? to, CancellationToken cancellationToken = default)
    {
        var today = DateTime.UtcNow.Date;
        var periodEnd = (to ?? today).Date;
        var periodStart = (from ?? periodEnd.AddDays(-29)).Date;
        if (periodStart > periodEnd) (periodStart, periodEnd) = (periodEnd, periodStart);

        var days = (periodEnd - periodStart).Days + 1;
        var prevEnd = periodStart.AddDays(-1);
        var prevStart = prevEnd.AddDays(-(days - 1));

        using var connection = OpenConnection();
        var org = new { OrganizationId = organizationId };

        var totals = await connection.QuerySingleAsync<(int Total, int Active)>(new CommandDefinition(@"
SELECT COUNT(1) AS Total,
       SUM(CASE WHEN employment_status = 'Active' THEN 1 ELSE 0 END) AS Active
FROM dbo.nh_hr_employees
WHERE organization_id = @OrganizationId AND archived_at IS NULL;", org, cancellationToken: cancellationToken));

        var summary = new EmployeeSummaryDto
        {
            TotalEmployees = totals.Total,
            ActiveEmployees = totals.Active
        };

        summary.ByStatus = await GroupCountAsync(connection, @"
SELECT e.employment_status AS [Key], COUNT(1) AS [Count]
FROM dbo.nh_hr_employees e
WHERE e.organization_id = @OrganizationId AND e.archived_at IS NULL
GROUP BY e.employment_status;", org, cancellationToken);

        summary.ByDepartment = await GroupCountAsync(connection, @"
SELECT COALESCE(d.name, 'Unassigned') AS [Key], COUNT(1) AS [Count]
FROM dbo.nh_hr_employees e
LEFT JOIN dbo.nh_hr_departments d ON d.id = e.department_id AND d.organization_id = e.organization_id
WHERE e.organization_id = @OrganizationId AND e.archived_at IS NULL
GROUP BY COALESCE(d.name, 'Unassigned');", org, cancellationToken);

        summary.ByLocation = await GroupCountAsync(connection, @"
SELECT COALESCE(l.name, 'Unassigned') AS [Key], COUNT(1) AS [Count]
FROM dbo.nh_hr_employees e
LEFT JOIN dbo.nh_hr_work_locations l ON l.id = e.work_location_id AND l.organization_id = e.organization_id
WHERE e.organization_id = @OrganizationId AND e.archived_at IS NULL
GROUP BY COALESCE(l.name, 'Unassigned');", org, cancellationToken);

        summary.ByEmploymentType = await GroupCountAsync(connection, @"
SELECT COALESCE(t.name, 'Unassigned') AS [Key], COUNT(1) AS [Count]
FROM dbo.nh_hr_employees e
LEFT JOIN dbo.nh_hr_employment_types t ON t.id = e.employment_type_id AND t.organization_id = e.organization_id
WHERE e.organization_id = @OrganizationId AND e.archived_at IS NULL
GROUP BY COALESCE(t.name, 'Unassigned');", org, cancellationToken);

        summary.NewHires = await CountNewHiresAsync(connection, organizationId, periodStart, periodEnd, cancellationToken);
        summary.PreviousNewHires = await CountNewHiresAsync(connection, organizationId, prevStart, prevEnd, cancellationToken);

        summary.TurnoverRate = await ComputeTurnoverAsync(connection, organizationId, periodStart, periodEnd, cancellationToken);
        summary.PreviousTurnoverRate = await ComputeTurnoverAsync(connection, organizationId, prevStart, prevEnd, cancellationToken);

        summary.AverageTenureYears = await AverageTenureAsync(connection, organizationId, today, activeStatusOnly: true, cancellationToken);
        summary.PreviousAverageTenureYears = await AverageTenureAsync(connection, organizationId, prevEnd, activeStatusOnly: false, cancellationToken);

        return summary;
    }

    // -------------------------------------------------------------------------
    // Lookups
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<HrLookupDto>> ListDepartmentsAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT id AS Id, name AS Name, code AS Code, is_active AS IsActive
FROM dbo.nh_hr_departments WHERE organization_id = @OrganizationId ORDER BY name;";
        using var connection = OpenConnection();
        return (await connection.QueryAsync<HrLookupDto>(new CommandDefinition(sql, new { OrganizationId = organizationId }, cancellationToken: cancellationToken))).ToList();
    }

    public async Task<IReadOnlyList<HrLookupDto>> ListJobTitlesAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT id AS Id, name AS Name, CAST(NULL AS nvarchar(50)) AS Code, is_active AS IsActive
FROM dbo.nh_hr_job_titles WHERE organization_id = @OrganizationId ORDER BY name;";
        using var connection = OpenConnection();
        return (await connection.QueryAsync<HrLookupDto>(new CommandDefinition(sql, new { OrganizationId = organizationId }, cancellationToken: cancellationToken))).ToList();
    }

    public async Task<IReadOnlyList<WorkLocationDto>> ListLocationsAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT id AS Id, name AS Name, CAST(NULL AS nvarchar(50)) AS Code, is_active AS IsActive,
       city AS City, state AS State, country AS Country
FROM dbo.nh_hr_work_locations WHERE organization_id = @OrganizationId ORDER BY name;";
        using var connection = OpenConnection();
        return (await connection.QueryAsync<WorkLocationDto>(new CommandDefinition(sql, new { OrganizationId = organizationId }, cancellationToken: cancellationToken))).ToList();
    }

    public async Task<IReadOnlyList<HrLookupDto>> ListEmploymentTypesAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT id AS Id, name AS Name, code AS Code, is_active AS IsActive
FROM dbo.nh_hr_employment_types WHERE organization_id = @OrganizationId ORDER BY sort_order, name;";
        using var connection = OpenConnection();
        return (await connection.QueryAsync<HrLookupDto>(new CommandDefinition(sql, new { OrganizationId = organizationId }, cancellationToken: cancellationToken))).ToList();
    }

    public async Task<Guid> UpsertDepartmentAsync(Guid organizationId, CreateHrLookupRequest request, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        return await UpsertNamedLookupAsync(connection, null, "nh_hr_departments", organizationId, request.Name, Trim(request.Code), actorUserId, cancellationToken);
    }

    public async Task<Guid> UpsertJobTitleAsync(Guid organizationId, CreateHrLookupRequest request, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        return await UpsertNamedLookupAsync(connection, null, "nh_hr_job_titles", organizationId, request.Name, null, actorUserId, cancellationToken);
    }

    public async Task<Guid> UpsertLocationAsync(Guid organizationId, CreateHrLookupRequest request, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        return await UpsertLocationCoreAsync(connection, null, organizationId, request, actorUserId, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Legacy ATS lookups
    // -------------------------------------------------------------------------

    public async Task<(string? FirstName, string? LastName, string? Email, string? Phone, string? ResumeUrl)?> GetCandidateBasicsAsync(
        Guid organizationId,
        Guid candidateId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT first_name AS FirstName, last_name AS LastName, email AS Email, phone AS Phone, resume_url AS ResumeUrl
FROM dbo.candidates
WHERE org_id = @OrganizationId AND id = @CandidateId;";

        using var connection = OpenConnection();
        var row = await connection.QuerySingleOrDefaultAsync<CandidateBasicsRow>(new CommandDefinition(sql,
            new { OrganizationId = organizationId, CandidateId = candidateId }, cancellationToken: cancellationToken));
        if (row == null) return null;
        return (row.FirstName, row.LastName, row.Email, row.Phone, row.ResumeUrl);
    }

    public async Task<(Guid ApplicationId, Guid CandidateId, string? StageName, string? Status)?> GetApplicationHireInfoAsync(
        Guid organizationId,
        Guid applicationId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT a.id AS ApplicationId, a.candidate_id AS CandidateId, ps.name AS StageName, a.status AS Status
FROM dbo.applications a
LEFT JOIN dbo.pipeline_stages ps ON ps.id = a.current_stage_id AND ps.org_id = a.org_id
WHERE a.org_id = @OrganizationId AND a.id = @ApplicationId;";

        using var connection = OpenConnection();
        var row = await connection.QuerySingleOrDefaultAsync<ApplicationHireRow>(new CommandDefinition(sql,
            new { OrganizationId = organizationId, ApplicationId = applicationId }, cancellationToken: cancellationToken));
        if (row == null) return null;
        return (row.ApplicationId, row.CandidateId, row.StageName, row.Status);
    }

    public async Task<(Guid ApplicationId, string? StageName, string? Status)?> FindHiredApplicationForCandidateAsync(
        Guid organizationId,
        Guid candidateId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT TOP 1 a.id AS ApplicationId, a.candidate_id AS CandidateId, ps.name AS StageName, a.status AS Status
FROM dbo.applications a
LEFT JOIN dbo.pipeline_stages ps ON ps.id = a.current_stage_id AND ps.org_id = a.org_id
WHERE a.org_id = @OrganizationId
  AND a.candidate_id = @CandidateId
  AND (LOWER(ps.name) = 'hired' OR LOWER(a.status) = 'hired')
ORDER BY a.updated_at DESC;";

        using var connection = OpenConnection();
        var row = await connection.QuerySingleOrDefaultAsync<ApplicationHireRow>(new CommandDefinition(sql,
            new { OrganizationId = organizationId, CandidateId = candidateId }, cancellationToken: cancellationToken));
        if (row == null) return null;
        return (row.ApplicationId, row.StageName, row.Status);
    }

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------

    private IDbConnection OpenConnection()
    {
        var connection = _connectionFactory.CreateConnection();
        connection.Open();
        return connection;
    }

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
            $"Core HR employees schema script not found. Expected sql/{SchemaScriptFileName}.");
    }

    internal static string ComputeDisplayName(string firstName, string lastName, string? preferredName)
    {
        if (!string.IsNullOrWhiteSpace(preferredName)) return preferredName.Trim();
        return $"{firstName} {lastName}".Trim();
    }

    private static double? ComputeTenureYears(DateTime? hireDate, DateTime? terminationDate)
    {
        if (!hireDate.HasValue) return null;
        var end = terminationDate ?? DateTime.UtcNow.Date;
        if (end < hireDate.Value) return 0;
        return Math.Round((end - hireDate.Value).TotalDays / 365.25, 2);
    }

    private static string BuildOrderBy(string? sort, string? dir)
    {
        var direction = string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
        var key = string.IsNullOrWhiteSpace(sort) ? "name" : sort.Trim();
        if (!SortColumns.TryGetValue(key, out var template))
            template = SortColumns["name"];
        return template.Replace("{dir}", direction, StringComparison.Ordinal);
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("%", "\\%", StringComparison.Ordinal)
             .Replace("_", "\\_", StringComparison.Ordinal)
             .Replace("[", "\\[", StringComparison.Ordinal);

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Slug(string value) =>
        new string(value.ToLowerInvariant().Where(char.IsLetter).ToArray());

    private static object BuildEmployeeParameters(
        Guid organizationId,
        Guid employeeId,
        CreateEmployeeRequest r,
        string? employeeNumber,
        Guid? actorUserId,
        DateTimeOffset now) => new
        {
            OrganizationId = organizationId,
            EmployeeId = employeeId,
            EmployeeNumber = employeeNumber,
            FirstName = r.FirstName.Trim(),
            MiddleName = Trim(r.MiddleName),
            LastName = r.LastName.Trim(),
            PreferredName = Trim(r.PreferredName),
            ProfilePictureUrl = Trim(r.ProfilePictureUrl),
            DateOfBirth = r.DateOfBirth?.Date,
            Nationality = Trim(r.Nationality),
            PersonalEmail = Trim(r.PersonalEmail),
            PersonalPhone = Trim(r.PersonalPhone),
            AddressLine1 = Trim(r.AddressLine1),
            AddressLine2 = Trim(r.AddressLine2),
            City = Trim(r.City),
            State = Trim(r.State),
            Country = Trim(r.Country),
            PostalCode = Trim(r.PostalCode),
            WorkEmail = Trim(r.WorkEmail),
            WorkPhone = Trim(r.WorkPhone),
            r.JobTitleId,
            r.DepartmentId,
            r.ManagerId,
            r.EmploymentTypeId,
            EmploymentStatus = Trim(r.EmploymentStatus),
            HireDate = r.HireDate?.Date,
            r.WorkLocationId,
            WorkModality = Trim(r.WorkModality),
            ProbationEndDate = r.ProbationEndDate?.Date,
            ContractStartDate = r.ContractStartDate?.Date,
            ContractEndDate = r.ContractEndDate?.Date,
            r.CandidateId,
            r.ApplicationId,
            Now = now,
            ActorUserId = actorUserId
        };

    private static async Task InsertEmployeeCoreAsync(
        IDbConnection connection,
        IDbTransaction? tx,
        Guid organizationId,
        Guid employeeId,
        CreateEmployeeRequest request,
        string employeeNumber,
        Guid? actorUserId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const string sql = @"
INSERT INTO dbo.nh_hr_employees (
    id, organization_id, employee_number, first_name, middle_name, last_name, preferred_name, profile_picture_url,
    date_of_birth, nationality, personal_email, personal_phone,
    address_line1, address_line2, city, state, country, postal_code,
    work_email, work_phone, job_title_id, department_id, manager_id, employment_type_id,
    employment_status, hire_date, work_location_id, work_modality,
    probation_end_date, contract_start_date, contract_end_date,
    candidate_id, application_id, created_at, created_by
) VALUES (
    @EmployeeId, @OrganizationId, @EmployeeNumber, @FirstName, @MiddleName, @LastName, @PreferredName, @ProfilePictureUrl,
    @DateOfBirth, @Nationality, @PersonalEmail, @PersonalPhone,
    @AddressLine1, @AddressLine2, @City, @State, @Country, @PostalCode,
    @WorkEmail, @WorkPhone, @JobTitleId, @DepartmentId, @ManagerId, @EmploymentTypeId,
    COALESCE(@EmploymentStatus, 'Pending'), @HireDate, @WorkLocationId, @WorkModality,
    @ProbationEndDate, @ContractStartDate, @ContractEndDate,
    @CandidateId, @ApplicationId, @Now, @ActorUserId
);";

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            BuildEmployeeParameters(organizationId, employeeId, request, employeeNumber.Trim(), actorUserId, now),
            tx,
            cancellationToken: cancellationToken));
    }

    private static async Task InsertActivityCoreAsync(
        IDbConnection connection,
        IDbTransaction? tx,
        Guid organizationId,
        Guid employeeId,
        string activityType,
        string summary,
        string? detailsJson,
        Guid? actorUserId,
        string? actorName,
        CancellationToken cancellationToken)
    {
        const string sql = @"
INSERT INTO dbo.nh_hr_employee_activity
    (id, organization_id, employee_id, activity_type, summary, details_json, created_at, created_by, created_by_name)
VALUES
    (@Id, @OrganizationId, @EmployeeId, @ActivityType, @Summary, @DetailsJson, @Now, @ActorUserId, @ActorName);";

        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            EmployeeId = employeeId,
            ActivityType = activityType,
            Summary = summary.Length > 500 ? summary[..500] : summary,
            DetailsJson = Trim(detailsJson),
            Now = DateTimeOffset.UtcNow,
            ActorUserId = actorUserId,
            ActorName = Trim(actorName)
        }, tx, cancellationToken: cancellationToken));
    }

    private static async Task InsertHistoryCoreAsync(
        IDbConnection connection,
        IDbTransaction? tx,
        Guid organizationId,
        Guid employeeId,
        string changeType,
        CreateEmployeeRequest snapshot,
        Guid? actorUserId,
        string? notes,
        DateTimeOffset effectiveAt,
        CancellationToken cancellationToken)
    {
        const string sql = @"
INSERT INTO dbo.nh_hr_employee_employment_history
    (id, organization_id, employee_id, effective_at, change_type, job_title_id, department_id,
     employment_type_id, employment_status, work_location_id, manager_id, notes, created_at, created_by)
VALUES
    (@Id, @OrganizationId, @EmployeeId, @EffectiveAt, @ChangeType, @JobTitleId, @DepartmentId,
     @EmploymentTypeId, @EmploymentStatus, @WorkLocationId, @ManagerId, @Notes, @Now, @ActorUserId);";

        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            EmployeeId = employeeId,
            EffectiveAt = effectiveAt,
            ChangeType = changeType,
            snapshot.JobTitleId,
            snapshot.DepartmentId,
            snapshot.EmploymentTypeId,
            EmploymentStatus = Trim(snapshot.EmploymentStatus),
            snapshot.WorkLocationId,
            snapshot.ManagerId,
            Notes = Trim(notes),
            Now = DateTimeOffset.UtcNow,
            ActorUserId = actorUserId
        }, tx, cancellationToken: cancellationToken));
    }

    private static async Task<IReadOnlyList<EmergencyContactDto>> ListEmergencyContactsCoreAsync(
        IDbConnection connection,
        Guid organizationId,
        Guid employeeId,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT id AS Id, full_name AS FullName, relationship AS Relationship, phone AS Phone, email AS Email, is_primary AS IsPrimary
FROM dbo.nh_hr_employee_emergency_contacts
WHERE organization_id = @OrganizationId AND employee_id = @EmployeeId
ORDER BY is_primary DESC, created_at ASC;";

        var rows = await connection.QueryAsync<EmergencyContactDto>(new CommandDefinition(sql,
            new { OrganizationId = organizationId, EmployeeId = employeeId }, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    private static async Task EnsureOrgDefaultsCoreAsync(
        IDbConnection connection,
        IDbTransaction? tx,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        const string sql = @"
IF NOT EXISTS (SELECT 1 FROM dbo.nh_hr_employment_types WHERE organization_id = @OrganizationId AND code = @Code)
    INSERT INTO dbo.nh_hr_employment_types (id, organization_id, code, name, is_active, sort_order, created_at)
    VALUES (@Id, @OrganizationId, @Code, @Name, 1, @SortOrder, @Now);";

        foreach (var (code, name, sort) in DefaultEmploymentTypes)
        {
            await connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                Code = code,
                Name = name,
                SortOrder = sort,
                Now = DateTimeOffset.UtcNow
            }, tx, cancellationToken: cancellationToken));
        }
    }

    /// <summary>
    /// Find-or-create (and reactivate / update code) for departments and job titles.
    /// <paramref name="table"/> is always an internal constant, never user input.
    /// </summary>
    private static async Task<Guid> UpsertNamedLookupAsync(
        IDbConnection connection,
        IDbTransaction? tx,
        string table,
        Guid organizationId,
        string name,
        string? code,
        Guid? actorUserId,
        CancellationToken cancellationToken)
    {
        if (table is not ("nh_hr_departments" or "nh_hr_job_titles"))
            throw new ArgumentException("Unsupported lookup table.", nameof(table));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));

        var trimmed = name.Trim();
        var hasCode = table == "nh_hr_departments";

        var existing = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            $"SELECT id FROM dbo.{table} WHERE organization_id = @OrganizationId AND name = @Name;",
            new { OrganizationId = organizationId, Name = trimmed }, tx, cancellationToken: cancellationToken));

        var now = DateTimeOffset.UtcNow;
        if (existing.HasValue)
        {
            var updateSql = hasCode
                ? $"UPDATE dbo.{table} SET is_active = 1, code = COALESCE(@Code, code), updated_at = @Now, updated_by = @ActorUserId WHERE organization_id = @OrganizationId AND id = @Id;"
                : $"UPDATE dbo.{table} SET is_active = 1, updated_at = @Now, updated_by = @ActorUserId WHERE organization_id = @OrganizationId AND id = @Id;";
            await connection.ExecuteAsync(new CommandDefinition(updateSql,
                new { OrganizationId = organizationId, Id = existing.Value, Code = code, Now = now, ActorUserId = actorUserId },
                tx, cancellationToken: cancellationToken));
            return existing.Value;
        }

        var id = Guid.NewGuid();
        var insertSql = hasCode
            ? $"INSERT INTO dbo.{table} (id, organization_id, name, code, is_active, created_at, created_by) VALUES (@Id, @OrganizationId, @Name, @Code, 1, @Now, @ActorUserId);"
            : $"INSERT INTO dbo.{table} (id, organization_id, name, is_active, created_at, created_by) VALUES (@Id, @OrganizationId, @Name, 1, @Now, @ActorUserId);";
        await connection.ExecuteAsync(new CommandDefinition(insertSql,
            new { Id = id, OrganizationId = organizationId, Name = trimmed, Code = code, Now = now, ActorUserId = actorUserId },
            tx, cancellationToken: cancellationToken));
        return id;
    }

    private static async Task<Guid> UpsertLocationCoreAsync(
        IDbConnection connection,
        IDbTransaction? tx,
        Guid organizationId,
        CreateHrLookupRequest request,
        Guid? actorUserId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("Name is required.", nameof(request));

        var name = request.Name.Trim();
        var existing = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM dbo.nh_hr_work_locations WHERE organization_id = @OrganizationId AND name = @Name;",
            new { OrganizationId = organizationId, Name = name }, tx, cancellationToken: cancellationToken));

        var now = DateTimeOffset.UtcNow;
        if (existing.HasValue)
        {
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_work_locations
SET is_active = 1,
    city = COALESCE(@City, city), state = COALESCE(@State, state), country = COALESCE(@Country, country),
    updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND id = @Id;",
                new
                {
                    OrganizationId = organizationId,
                    Id = existing.Value,
                    City = Trim(request.City),
                    State = Trim(request.State),
                    Country = Trim(request.Country),
                    Now = now,
                    ActorUserId = actorUserId
                }, tx, cancellationToken: cancellationToken));
            return existing.Value;
        }

        var id = Guid.NewGuid();
        await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_work_locations (id, organization_id, name, city, state, country, is_active, created_at, created_by)
VALUES (@Id, @OrganizationId, @Name, @City, @State, @Country, 1, @Now, @ActorUserId);",
            new
            {
                Id = id,
                OrganizationId = organizationId,
                Name = name,
                City = Trim(request.City),
                State = Trim(request.State),
                Country = Trim(request.Country),
                Now = now,
                ActorUserId = actorUserId
            }, tx, cancellationToken: cancellationToken));
        return id;
    }

    private static async Task<Dictionary<string, int>> GroupCountAsync(
        IDbConnection connection,
        string sql,
        object parameters,
        CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<GroupCountRow>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        return rows.ToDictionary(r => r.Key, r => r.Count, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<int> CountNewHiresAsync(
        IDbConnection connection,
        Guid organizationId,
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken)
    {
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(1) FROM dbo.nh_hr_employees
WHERE organization_id = @OrganizationId AND archived_at IS NULL
  AND hire_date >= @Start AND hire_date <= @End;",
            new { OrganizationId = organizationId, Start = start, End = end }, cancellationToken: cancellationToken));
    }

    /// <summary>Headcount on a given date (hired on/before it and not terminated on/before it).</summary>
    private static Task<int> HeadcountAtAsync(
        IDbConnection connection,
        Guid organizationId,
        DateTime asOf,
        CancellationToken cancellationToken)
    {
        return connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(1) FROM dbo.nh_hr_employees
WHERE organization_id = @OrganizationId AND archived_at IS NULL
  AND COALESCE(hire_date, CAST(created_at AS date)) <= @AsOf
  AND (termination_date IS NULL OR termination_date > @AsOf);",
            new { OrganizationId = organizationId, AsOf = asOf }, cancellationToken: cancellationToken));
    }

    /// <summary>Turnover as a percentage: terminations in period / average headcount * 100.</summary>
    private static async Task<double> ComputeTurnoverAsync(
        IDbConnection connection,
        Guid organizationId,
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken)
    {
        var terminations = await connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(1) FROM dbo.nh_hr_employees
WHERE organization_id = @OrganizationId AND archived_at IS NULL
  AND employment_status = 'Terminated'
  AND termination_date >= @Start AND termination_date <= @End;",
            new { OrganizationId = organizationId, Start = start, End = end }, cancellationToken: cancellationToken));

        var startHeadcount = await HeadcountAtAsync(connection, organizationId, start.AddDays(-1), cancellationToken);
        var endHeadcount = await HeadcountAtAsync(connection, organizationId, end, cancellationToken);
        var average = (startHeadcount + endHeadcount) / 2.0;
        if (average <= 0) return 0;
        return Math.Round(terminations / average * 100.0, 2);
    }

    private static async Task<double> AverageTenureAsync(
        IDbConnection connection,
        Guid organizationId,
        DateTime asOf,
        bool activeStatusOnly,
        CancellationToken cancellationToken)
    {
        var sql = @"
SELECT AVG(CAST(DATEDIFF(day, hire_date, @AsOf) AS float)) / 365.25
FROM dbo.nh_hr_employees
WHERE organization_id = @OrganizationId AND archived_at IS NULL
  AND hire_date IS NOT NULL AND hire_date <= @AsOf
  AND (@ActiveOnly = 1 AND employment_status = 'Active'
       OR @ActiveOnly = 0 AND (termination_date IS NULL OR termination_date > @AsOf));";

        var value = await connection.ExecuteScalarAsync<double?>(new CommandDefinition(sql,
            new { OrganizationId = organizationId, AsOf = asOf, ActiveOnly = activeStatusOnly ? 1 : 0 },
            cancellationToken: cancellationToken));
        return value.HasValue ? Math.Round(value.Value, 2) : 0;
    }

    // -------------------------------------------------------------------------
    // SQL fragments / row types
    // -------------------------------------------------------------------------

    private const string SelectListColumns = @"
SELECT
    e.id AS Id,
    e.employee_number AS EmployeeNumber,
    e.first_name AS FirstName,
    e.last_name AS LastName,
    e.preferred_name AS PreferredName,
    e.profile_picture_url AS ProfilePictureUrl,
    e.work_email AS WorkEmail,
    jt.name AS JobTitle,
    e.job_title_id AS JobTitleId,
    d.name AS Department,
    e.department_id AS DepartmentId,
    wl.name AS Location,
    e.work_location_id AS WorkLocationId,
    e.employment_status AS EmploymentStatus,
    et.name AS EmploymentType,
    e.employment_type_id AS EmploymentTypeId,
    e.manager_id AS ManagerId,
    CASE WHEN m.id IS NULL THEN NULL
         ELSE COALESCE(NULLIF(LTRIM(RTRIM(m.preferred_name)), ''), m.first_name + ' ' + m.last_name) END AS ManagerName,
    e.hire_date AS HireDate,
    CAST(CASE WHEN e.archived_at IS NULL THEN 0 ELSE 1 END AS bit) AS IsArchived";

    private const string SelectDetailColumns = SelectListColumns + @",
    e.middle_name AS MiddleName,
    e.date_of_birth AS DateOfBirth,
    e.nationality AS Nationality,
    e.personal_email AS PersonalEmail,
    e.personal_phone AS PersonalPhone,
    e.address_line1 AS AddressLine1,
    e.address_line2 AS AddressLine2,
    e.city AS City,
    e.state AS State,
    e.country AS Country,
    e.postal_code AS PostalCode,
    e.work_phone AS WorkPhone,
    e.work_modality AS WorkModality,
    e.termination_date AS TerminationDate,
    e.termination_reason AS TerminationReason,
    e.probation_end_date AS ProbationEndDate,
    e.contract_start_date AS ContractStartDate,
    e.contract_end_date AS ContractEndDate,
    e.candidate_id AS CandidateId,
    e.application_id AS ApplicationId,
    e.linked_user_id AS LinkedUserId,
    e.created_at AS CreatedAt,
    e.updated_at AS UpdatedAt,
    e.archived_at AS ArchivedAt";

    private const string DocumentSelect = @"
SELECT id AS Id, employee_id AS EmployeeId, category AS Category, name AS Name,
       document_id AS DocumentId, external_url AS ExternalUrl, status AS Status,
       expires_at AS ExpiresAt, created_at AS CreatedAt
FROM dbo.nh_hr_employee_documents";

    private sealed class CandidateBasicsRow
    {
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string? ResumeUrl { get; set; }
    }

    private sealed class ApplicationHireRow
    {
        public Guid ApplicationId { get; set; }
        public Guid CandidateId { get; set; }
        public string? StageName { get; set; }
        public string? Status { get; set; }
    }

    private sealed class GroupCountRow
    {
        public string Key { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    // -------------------------------------------------------------------------
    // Demo data (fictional)
    // -------------------------------------------------------------------------

    private static readonly string[] DemoDepartments = ["Operations", "Sales", "Administration", "Hospitality", "IT"];

    private static readonly (string Dept, int Level, string Title)[] DemoJobTitles =
    [
        ("Operations", 0, "Operations Manager"), ("Operations", 1, "Operations Coordinator"), ("Operations", 2, "Logistics Analyst"),
        ("Sales", 0, "Sales Director"), ("Sales", 1, "Account Executive"), ("Sales", 2, "Sales Development Representative"),
        ("Administration", 0, "Administration Manager"), ("Administration", 1, "HR Generalist"), ("Administration", 2, "Office Assistant"),
        ("Hospitality", 0, "Guest Services Manager"), ("Hospitality", 1, "Front Desk Supervisor"), ("Hospitality", 2, "Concierge"),
        ("IT", 0, "IT Manager"), ("IT", 1, "Software Engineer"), ("IT", 2, "Support Technician")
    ];

    private static readonly (string Name, string City, string State, string Country)[] DemoLocations =
    [
        ("Mexico City HQ", "Mexico City", "CDMX", "Mexico"),
        ("Guadalajara Office", "Guadalajara", "Jalisco", "Mexico"),
        ("Monterrey Hub", "Monterrey", "Nuevo Leon", "Mexico"),
        ("Remote", "Remote", "", "Mexico")
    ];

    private sealed record DemoEmployee(
        string First, string Last, string? Preferred, string Dept, int TitleLevel, bool IsManager,
        string Location, string Status, string TypeCode, int HiredDaysAgo);

    // Managers (IsManager = true) for each department come first so reports can reference them.
    private static readonly DemoEmployee[] DemoEmployees =
    [
        new("Valeria", "Ortega", null, "Operations", 0, true, "Mexico City HQ", "Active", "FullTime", 2200),
        new("Rodrigo", "Salinas", "Rod", "Sales", 0, true, "Mexico City HQ", "Active", "FullTime", 1900),
        new("Mariana", "Fuentes", null, "Administration", 0, true, "Mexico City HQ", "Active", "FullTime", 2500),
        new("Alejandro", "Cardenas", "Alex", "Hospitality", 0, true, "Guadalajara Office", "Active", "FullTime", 1700),
        new("Fernanda", "Lozano", null, "IT", 0, true, "Remote", "Active", "FullTime", 1500),

        new("Diego", "Almanza", null, "Operations", 1, false, "Mexico City HQ", "Active", "FullTime", 1200),
        new("Camila", "Rojas", null, "Operations", 2, false, "Monterrey Hub", "Active", "FullTime", 800),
        new("Sebastian", "Herrera", "Seb", "Operations", 2, false, "Monterrey Hub", "OnLeave", "FullTime", 600),
        new("Ximena", "Prado", null, "Operations", 1, false, "Guadalajara Office", "Active", "PartTime", 420),
        new("Emilio", "Barrera", null, "Operations", 2, false, "Mexico City HQ", "Onboarding", "FullTime", 20),

        new("Regina", "Montoya", null, "Sales", 1, false, "Mexico City HQ", "Active", "FullTime", 1000),
        new("Mateo", "Villarreal", null, "Sales", 1, false, "Monterrey Hub", "Active", "FullTime", 700),
        new("Daniela", "Cervantes", "Dani", "Sales", 2, false, "Remote", "Active", "Contractor", 365),
        new("Joaquin", "Esquivel", null, "Sales", 2, false, "Guadalajara Office", "Terminated", "FullTime", 900),
        new("Paola", "Dominguez", null, "Sales", 2, false, "Mexico City HQ", "Pending", "FullTime", 5),

        new("Andres", "Quintero", null, "Administration", 1, false, "Mexico City HQ", "Active", "FullTime", 1400),
        new("Lucia", "Navarro", null, "Administration", 2, false, "Mexico City HQ", "Active", "PartTime", 300),
        new("Ivan", "Zamora", null, "Administration", 2, false, "Guadalajara Office", "Inactive", "Temporary", 450),
        new("Renata", "Ibarra", null, "Administration", 1, false, "Remote", "Active", "FullTime", 240),

        new("Santiago", "Maldonado", "Santi", "Hospitality", 1, false, "Guadalajara Office", "Active", "FullTime", 1100),
        new("Natalia", "Escobar", null, "Hospitality", 2, false, "Guadalajara Office", "Active", "FullTime", 650),
        new("Gael", "Pineda", null, "Hospitality", 2, false, "Mexico City HQ", "Active", "Temporary", 180),
        new("Julieta", "Carrillo", null, "Hospitality", 2, false, "Monterrey Hub", "OnLeave", "FullTime", 520),
        new("Bruno", "Alvarado", null, "Hospitality", 2, false, "Guadalajara Office", "Terminated", "PartTime", 700),

        new("Isabella", "Mejia", "Isa", "IT", 1, false, "Remote", "Active", "FullTime", 950),
        new("Leonardo", "Sandoval", "Leo", "IT", 1, false, "Remote", "Active", "FullTime", 500),
        new("Valentina", "Reyes", null, "IT", 2, false, "Mexico City HQ", "Active", "FullTime", 330),
        new("Tomas", "Aguilar", null, "IT", 2, false, "Monterrey Hub", "Active", "Intern", 90),
        new("Sofia", "Padilla", null, "IT", 1, false, "Remote", "Onboarding", "Contractor", 12)
    ];
}
