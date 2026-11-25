using System.Data;
using System.Data.SqlClient;
using System.Text.Json;
using Dapper;
using nexthire_api.Data;
using nexthire_api.DTOs;
using nexthire_api.DTOs.CoreHr;
using nexthire_api.Services;

namespace nexthire_api.Repositories;

/// <summary>
/// Dapper/SQL Server implementation of Core HR onboarding persistence.
/// All queries are parameterized and scoped by organization_id.
/// </summary>
public class OnboardingRepository : IOnboardingRepository
{
    private const string SchemaScriptFileName = "010-core-hr-onboarding-schema.sql";

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<OnboardingRepository> _logger;

    public OnboardingRepository(IDbConnectionFactory connectionFactory, ILogger<OnboardingRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // Shared SQL fragments
    // -------------------------------------------------------------------------

    private const string EmployeeNameSql =
        "CASE WHEN NULLIF(LTRIM(RTRIM(ISNULL(e.preferred_name, ''))), '') IS NULL " +
        "THEN LTRIM(RTRIM(e.first_name + ' ' + e.last_name)) ELSE LTRIM(RTRIM(e.preferred_name)) END";

    private const string OnboardingSelect = @"
SELECT
    o.id AS Id,
    o.employee_id AS EmployeeId,
    e.employee_number AS EmployeeNumber,
    " + EmployeeNameSql + @" AS EmployeeName,
    e.work_email AS EmployeeEmail,
    e.profile_picture_url AS AvatarUrl,
    jt.name AS JobTitle,
    e.job_title_id AS JobTitleId,
    d.name AS Department,
    e.department_id AS DepartmentId,
    o.template_id AS TemplateId,
    t.name AS TemplateName,
    o.template_version AS TemplateVersion,
    o.owner_user_id AS OwnerUserId,
    COALESCE(u.display_name, u.email) AS OwnerName,
    o.start_date AS StartDate,
    o.expected_completion_date AS ExpectedCompletionDate,
    o.actual_completion_date AS ActualCompletionDate,
    o.status AS Status,
    o.notes AS Notes,
    o.cancel_reason AS CancelReason,
    o.created_at AS CreatedAt,
    o.updated_at AS UpdatedAt";

    private const string OnboardingJoins = @"
FROM dbo.nh_hr_employee_onboardings o
INNER JOIN dbo.nh_hr_employees e ON e.id = o.employee_id AND e.organization_id = o.organization_id
LEFT JOIN dbo.nh_hr_departments d ON d.id = e.department_id AND d.organization_id = e.organization_id
LEFT JOIN dbo.nh_hr_job_titles jt ON jt.id = e.job_title_id AND jt.organization_id = e.organization_id
LEFT JOIN dbo.nh_hr_onboarding_templates t ON t.id = o.template_id AND t.organization_id = o.organization_id
LEFT JOIN dbo.nh_users u ON u.id = o.owner_user_id AND u.org_id = o.organization_id";

    private const string OnboardingFilters = @"
WHERE o.organization_id = @OrganizationId
  AND (@Status IS NULL OR o.status = @Status)
  AND (@DepartmentId IS NULL OR e.department_id = @DepartmentId)
  AND (@OwnerUserId IS NULL OR o.owner_user_id = @OwnerUserId)
  AND (@TemplateId IS NULL OR o.template_id = @TemplateId)
  AND (@StartFrom IS NULL OR o.start_date >= @StartFrom)
  AND (@StartTo IS NULL OR o.start_date <= @StartTo)
  AND (@OverdueOnly = 0 OR (
        o.status IN ('NotStarted', 'InProgress', 'InReview')
        AND EXISTS (
            SELECT 1 FROM dbo.nh_hr_onboarding_tasks ot
            WHERE ot.organization_id = o.organization_id AND ot.onboarding_id = o.id
              AND ot.status NOT IN ('Cancelled', 'Completed') AND ot.is_waived = 0
              AND ot.due_date < @Today)))
  AND (
        @SearchLike IS NULL
        OR e.first_name LIKE @SearchLike ESCAPE '\'
        OR e.last_name LIKE @SearchLike ESCAPE '\'
        OR ISNULL(e.preferred_name, '') LIKE @SearchLike ESCAPE '\'
        OR (e.first_name + ' ' + e.last_name) LIKE @SearchLike ESCAPE '\'
        OR ISNULL(e.work_email, '') LIKE @SearchLike ESCAPE '\'
        OR e.employee_number LIKE @SearchLike ESCAPE '\'
        OR ISNULL(jt.name, '') LIKE @SearchLike ESCAPE '\'
        OR ISNULL(t.name, '') LIKE @SearchLike ESCAPE '\'
      )";

    private static readonly Dictionary<string, string> SortColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["employee"] = "e.last_name {dir}, e.first_name {dir}",
        ["startDate"] = "o.start_date {dir}",
        ["expectedCompletion"] = "o.expected_completion_date {dir}",
        ["status"] = "o.status {dir}",
        ["created"] = "o.created_at {dir}"
    };

    private const string TaskSelect = @"
SELECT
    t.id AS Id, t.onboarding_id AS OnboardingId, t.template_task_id AS TemplateTaskId,
    t.title AS Title, t.description AS Description, t.category AS Category,
    t.assignment_type AS AssignmentType, t.assigned_user_id AS AssignedUserId,
    COALESCE(u.display_name, u.email) AS AssignedUserName,
    t.due_date AS DueDate, t.status AS Status, t.is_required AS IsRequired,
    t.requires_document AS RequiresDocument, t.document_category AS DocumentCategory,
    t.requires_approval AS RequiresApproval, t.approval_status AS ApprovalStatus,
    t.approved_by AS ApprovedBy, t.approved_at AS ApprovedAt,
    t.completed_at AS CompletedAt, t.completed_by AS CompletedBy, t.completion_notes AS CompletionNotes,
    t.display_order AS DisplayOrder, t.depends_on_task_id AS DependsOnTaskId, t.ats_task_id AS AtsTaskId,
    t.is_waived AS IsWaived, t.created_at AS CreatedAt
FROM dbo.nh_hr_onboarding_tasks t
LEFT JOIN dbo.nh_users u ON u.id = t.assigned_user_id AND u.org_id = t.organization_id";

    private const string TaskStateSelect = @"
SELECT id AS Id, onboarding_id AS OnboardingId, title AS Title, status AS Status, is_waived AS IsWaived,
       is_required AS IsRequired, requires_approval AS RequiresApproval, approval_status AS ApprovalStatus,
       due_date AS DueDate, depends_on_task_id AS DependsOnTaskId
FROM dbo.nh_hr_onboarding_tasks";

    private const string DocumentSelect = @"
SELECT id AS Id, onboarding_id AS OnboardingId, onboarding_task_id AS OnboardingTaskId,
       employee_document_id AS EmployeeDocumentId, category AS Category, name AS Name,
       is_required AS IsRequired, status AS Status, created_at AS CreatedAt
FROM dbo.nh_hr_onboarding_documents";

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
        using var tx = connection.BeginTransaction();
        try
        {
            await EnsureOrgDefaultsCoreAsync(connection, tx, organizationId, cancellationToken);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private sealed record DemoScenario(string Status, int StartOffsetDays, double CompletionRatio, int? CompletedDaysAgo = null, string? CancelReason = null);

    private static readonly DemoScenario[] DemoScenarios =
    [
        new(OnboardingStatuses.NotStarted, 4, 0),
        new(OnboardingStatuses.InProgress, -6, 0.4),
        new(OnboardingStatuses.InProgress, -18, 0.25),     // behind schedule: overdue tasks
        new(OnboardingStatuses.InReview, -25, 1.0),
        new(OnboardingStatuses.Completed, -40, 1.0, CompletedDaysAgo: 5),
        new(OnboardingStatuses.Cancelled, -10, 0.1, CancelReason: "Candidate withdrew before the first day (demo data)")
    ];

    public async Task SeedDemoOnboardingsIfEmptyAsync(
        Guid organizationId,
        Guid? actorUserId,
        bool isDevelopment,
        CancellationToken cancellationToken = default)
    {
        if (!isDevelopment) return;

        using var connection = OpenConnection();

        var existing = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM dbo.nh_hr_employee_onboardings WHERE organization_id = @OrganizationId;",
            new { OrganizationId = organizationId }, cancellationToken: cancellationToken));
        if (existing > 0) return;

        var employeeIds = (await connection.QueryAsync<Guid>(new CommandDefinition(@"
SELECT TOP (@Take) e.id
FROM dbo.nh_hr_employees e
WHERE e.organization_id = @OrganizationId
  AND e.archived_at IS NULL
  AND e.employment_status IN ('Pending', 'Onboarding', 'Active')
ORDER BY CASE WHEN e.employment_status IN ('Pending', 'Onboarding') THEN 0 ELSE 1 END, e.hire_date DESC, e.id;",
            new { OrganizationId = organizationId, Take = DemoScenarios.Length }, cancellationToken: cancellationToken))).ToList();
        if (employeeIds.Count == 0) return;

        Guid? ownerUserId = null;
        try
        {
            ownerUserId = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT TOP 1 id FROM dbo.nh_users WHERE org_id = @OrganizationId ORDER BY created_at;",
                new { OrganizationId = organizationId }, cancellationToken: cancellationToken));
        }
        catch (SqlException ex)
        {
            _logger.LogDebug(ex, "Could not resolve a demo onboarding owner for organization {OrganizationId}.", organizationId);
        }

        using var tx = connection.BeginTransaction();
        try
        {
            await EnsureOrgDefaultsCoreAsync(connection, tx, organizationId, cancellationToken);

            // Re-check under the transaction so concurrent first requests do not double seed.
            var again = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(1) FROM dbo.nh_hr_employee_onboardings WITH (UPDLOCK, HOLDLOCK) WHERE organization_id = @OrganizationId;",
                new { OrganizationId = organizationId }, tx, cancellationToken: cancellationToken));
            if (again > 0)
            {
                tx.Rollback();
                return;
            }

            var today = DateTime.UtcNow.Date;
            var now = DateTimeOffset.UtcNow;

            for (var i = 0; i < employeeIds.Count; i++)
            {
                var scenario = DemoScenarios[i];
                var employeeId = employeeIds[i];
                var templateId = await ResolveTemplateIdCoreAsync(connection, tx, organizationId, employeeId, cancellationToken);
                var start = today.AddDays(scenario.StartOffsetDays);

                var created = await InsertOnboardingCoreAsync(connection, tx, organizationId, new CreateOnboardingCommand
                {
                    EmployeeId = employeeId,
                    TemplateId = templateId,
                    OwnerUserId = ownerUserId,
                    StartDate = start,
                    InitialStatus = scenario.Status == OnboardingStatuses.NotStarted ? OnboardingStatuses.NotStarted : OnboardingStatuses.InProgress,
                    Notes = "Demo onboarding",
                    ActorUserId = actorUserId,
                    ActorName = "System"
                }, cancellationToken);

                // Complete a share of the tasks (in display order) to produce realistic progress.
                var taskIds = (await connection.QueryAsync<Guid>(new CommandDefinition(
                    "SELECT id FROM dbo.nh_hr_onboarding_tasks WHERE organization_id = @OrganizationId AND onboarding_id = @Id ORDER BY display_order, created_at;",
                    new { OrganizationId = organizationId, Id = created.Id }, tx, cancellationToken: cancellationToken))).ToList();
                var toComplete = (int)Math.Round(taskIds.Count * scenario.CompletionRatio, MidpointRounding.AwayFromZero);
                var fullyApproved = scenario.CompletionRatio >= 1.0;
                foreach (var taskId in taskIds.Take(toComplete))
                {
                    await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_onboarding_tasks
SET status = 'Completed', completed_at = @Now, completed_by = @ActorUserId,
    approval_status = CASE WHEN requires_approval = 1 THEN @ApprovalStatus ELSE NULL END,
    approved_by = CASE WHEN requires_approval = 1 AND @ApprovalStatus = 'Approved' THEN @ActorUserId ELSE NULL END,
    approved_at = CASE WHEN requires_approval = 1 AND @ApprovalStatus = 'Approved' THEN @Now ELSE NULL END,
    updated_at = @Now
WHERE organization_id = @OrganizationId AND id = @Id;",
                        new
                        {
                            OrganizationId = organizationId, Id = taskId, Now = now, ActorUserId = actorUserId,
                            ApprovalStatus = fullyApproved ? OnboardingApprovalStatuses.Approved : OnboardingApprovalStatuses.Pending
                        }, tx, cancellationToken: cancellationToken));
                }

                if (scenario.Status is OnboardingStatuses.InReview or OnboardingStatuses.Completed or OnboardingStatuses.Cancelled)
                {
                    DateTime? completion = scenario.CompletedDaysAgo.HasValue ? today.AddDays(-scenario.CompletedDaysAgo.Value) : null;
                    await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_employee_onboardings
SET status = @Status, actual_completion_date = @CompletionDate, cancel_reason = @CancelReason, updated_at = @Now
WHERE organization_id = @OrganizationId AND id = @Id;",
                        new
                        {
                            OrganizationId = organizationId, Id = created.Id, Status = scenario.Status,
                            CompletionDate = completion, scenario.CancelReason, Now = now
                        }, tx, cancellationToken: cancellationToken));
                    await InsertActivityCoreAsync(connection, tx, organizationId, created.Id, "StatusChanged",
                        $"Status set to {scenario.Status} (demo data)", null, actorUserId, "System", cancellationToken);
                }
            }

            tx.Commit();
            _logger.LogInformation("Seeded {Count} demo onboardings for organization {OrganizationId}.", employeeIds.Count, organizationId);
        }
        catch
        {
            try { tx.Rollback(); } catch (InvalidOperationException) { /* already completed */ }
            throw;
        }
    }

    // -------------------------------------------------------------------------
    // Templates
    // -------------------------------------------------------------------------

    private const string TemplateSelect = @"
SELECT t.id AS Id, t.name AS Name, t.description AS Description,
       t.department_id AS DepartmentId, d.name AS Department,
       t.job_title_id AS JobTitleId, jt.name AS JobTitle,
       t.employment_type_id AS EmploymentTypeId, et.name AS EmploymentType,
       t.estimated_duration_days AS EstimatedDurationDays,
       t.is_active AS IsActive, t.is_default AS IsDefault, t.version AS Version,
       (SELECT COUNT(1) FROM dbo.nh_hr_onboarding_template_tasks tt
         WHERE tt.template_id = t.id AND tt.organization_id = t.organization_id) AS TaskCount,
       CAST(CASE WHEN t.archived_at IS NULL THEN 0 ELSE 1 END AS bit) AS IsArchived,
       t.created_at AS CreatedAt, t.updated_at AS UpdatedAt
FROM dbo.nh_hr_onboarding_templates t
LEFT JOIN dbo.nh_hr_departments d ON d.id = t.department_id AND d.organization_id = t.organization_id
LEFT JOIN dbo.nh_hr_job_titles jt ON jt.id = t.job_title_id AND jt.organization_id = t.organization_id
LEFT JOIN dbo.nh_hr_employment_types et ON et.id = t.employment_type_id AND et.organization_id = t.organization_id";

    public async Task<IReadOnlyList<OnboardingTemplateListItemDto>> ListTemplatesAsync(
        Guid organizationId, OnboardingTemplateListQuery query, CancellationToken cancellationToken = default)
    {
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : "%" + EscapeLike(query.Search.Trim()) + "%";
        var sql = TemplateSelect + @"
WHERE t.organization_id = @OrganizationId
  AND (@IncludeArchived = 1 OR t.archived_at IS NULL)
  AND (@IsActive IS NULL OR t.is_active = @IsActive)
  AND (@DepartmentId IS NULL OR t.department_id = @DepartmentId)
  AND (@SearchLike IS NULL OR t.name LIKE @SearchLike ESCAPE '\' OR ISNULL(t.description, '') LIKE @SearchLike ESCAPE '\')
ORDER BY t.is_default DESC, t.name;";

        using var connection = OpenConnection();
        return (await connection.QueryAsync<OnboardingTemplateListItemDto>(new CommandDefinition(sql, new
        {
            OrganizationId = organizationId,
            IncludeArchived = query.IncludeArchived ? 1 : 0,
            query.IsActive,
            query.DepartmentId,
            SearchLike = search
        }, cancellationToken: cancellationToken))).ToList();
    }

    public async Task<OnboardingTemplateDetailDto?> GetTemplateAsync(Guid organizationId, Guid templateId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        return await GetTemplateCoreAsync(connection, null, organizationId, templateId, cancellationToken);
    }

    public async Task<Guid> CreateTemplateAsync(Guid organizationId, UpsertOnboardingTemplateRequest request, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        try
        {
            var id = await InsertTemplateCoreAsync(connection, tx, organizationId, request, actorUserId, cancellationToken);
            tx.Commit();
            return id;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<bool> UpdateTemplateAsync(Guid organizationId, Guid templateId, UpsertOnboardingTemplateRequest request, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        try
        {
            if (request.IsDefault)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE dbo.nh_hr_onboarding_templates SET is_default = 0 WHERE organization_id = @OrganizationId AND id <> @Id AND is_default = 1;",
                    new { OrganizationId = organizationId, Id = templateId }, tx, cancellationToken: cancellationToken));
            }

            var updated = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_onboarding_templates
SET name = @Name, description = @Description, department_id = @DepartmentId, job_title_id = @JobTitleId,
    employment_type_id = @EmploymentTypeId, estimated_duration_days = @EstimatedDurationDays,
    is_active = @IsActive, is_default = @IsDefault, version = version + 1,
    updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND id = @Id AND archived_at IS NULL;",
                new
                {
                    OrganizationId = organizationId, Id = templateId,
                    Name = request.Name.Trim(), Description = Trim(request.Description),
                    request.DepartmentId, request.JobTitleId, request.EmploymentTypeId, request.EstimatedDurationDays,
                    request.IsActive, request.IsDefault, Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId
                }, tx, cancellationToken: cancellationToken));
            if (updated == 0)
            {
                tx.Rollback();
                return false;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.nh_hr_onboarding_template_tasks WHERE organization_id = @OrganizationId AND template_id = @Id;",
                new { OrganizationId = organizationId, Id = templateId }, tx, cancellationToken: cancellationToken));
            await InsertTemplateTasksCoreAsync(connection, tx, organizationId, templateId, request.Tasks, cancellationToken);

            tx.Commit();
            return true;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<Guid?> DuplicateTemplateAsync(Guid organizationId, Guid templateId, string newName, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var source = await GetTemplateCoreAsync(connection, null, organizationId, templateId, cancellationToken);
        if (source == null) return null;

        var request = new UpsertOnboardingTemplateRequest
        {
            Name = newName,
            Description = source.Description,
            DepartmentId = source.DepartmentId,
            JobTitleId = source.JobTitleId,
            EmploymentTypeId = source.EmploymentTypeId,
            EstimatedDurationDays = source.EstimatedDurationDays,
            IsActive = true,
            IsDefault = false,
            Tasks = source.Tasks.Select(t => new UpsertOnboardingTemplateTaskRequest
            {
                Id = t.Id,
                Title = t.Title,
                Description = t.Description,
                Category = t.Category,
                AssignmentType = t.AssignmentType,
                DefaultAssigneeUserId = t.DefaultAssigneeUserId,
                DueOffsetDays = t.DueOffsetDays,
                IsRequired = t.IsRequired,
                RequiresDocument = t.RequiresDocument,
                DocumentCategory = t.DocumentCategory,
                RequiresApproval = t.RequiresApproval,
                DependsOnTaskId = t.DependsOnTaskId
            }).ToList()
        };

        using var tx = connection.BeginTransaction();
        try
        {
            var id = await InsertTemplateCoreAsync(connection, tx, organizationId, request, actorUserId, cancellationToken);
            tx.Commit();
            return id;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<bool> ArchiveTemplateAsync(Guid organizationId, Guid templateId, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
UPDATE dbo.nh_hr_onboarding_templates
SET archived_at = @Now, archived_by = @ActorUserId, is_active = 0, is_default = 0, updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND id = @Id AND archived_at IS NULL;";

        using var connection = OpenConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(sql,
            new { OrganizationId = organizationId, Id = templateId, Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId },
            cancellationToken: cancellationToken));
        return affected > 0;
    }

    public async Task<bool> TemplateNameExistsAsync(Guid organizationId, string name, Guid? excludeId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT CASE WHEN EXISTS (
    SELECT 1 FROM dbo.nh_hr_onboarding_templates
    WHERE organization_id = @OrganizationId AND archived_at IS NULL AND name = @Name
      AND (@ExcludeId IS NULL OR id <> @ExcludeId)
) THEN 1 ELSE 0 END;";

        using var connection = OpenConnection();
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql,
            new { OrganizationId = organizationId, Name = name.Trim(), ExcludeId = excludeId }, cancellationToken: cancellationToken)) == 1;
    }

    public async Task<string?> CheckTemplateReferencesAsync(Guid organizationId, UpsertOnboardingTemplateRequest request, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();

        async Task<bool> Exists(string table, Guid id) =>
            await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                $"SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.{table} WHERE organization_id = @OrganizationId AND id = @Id) THEN 1 ELSE 0 END;",
                new { OrganizationId = organizationId, Id = id }, cancellationToken: cancellationToken)) == 1;

        if (request.DepartmentId.HasValue && !await Exists("nh_hr_departments", request.DepartmentId.Value)) return "Department not found.";
        if (request.JobTitleId.HasValue && !await Exists("nh_hr_job_titles", request.JobTitleId.Value)) return "Job title not found.";
        if (request.EmploymentTypeId.HasValue && !await Exists("nh_hr_employment_types", request.EmploymentTypeId.Value)) return "Employment type not found.";
        return null;
    }

    public async Task<Guid?> ResolveTemplateIdForEmployeeAsync(Guid organizationId, Guid employeeId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        return await ResolveTemplateIdCoreAsync(connection, null, organizationId, employeeId, cancellationToken);
    }

    private static async Task<Guid?> ResolveTemplateIdCoreAsync(
        IDbConnection connection, IDbTransaction? tx, Guid organizationId, Guid employeeId, CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT TOP 1 t.id
FROM dbo.nh_hr_onboarding_templates t
INNER JOIN dbo.nh_hr_employees e ON e.organization_id = t.organization_id AND e.id = @EmployeeId
LEFT JOIN dbo.nh_hr_departments d ON d.id = e.department_id AND d.organization_id = e.organization_id
WHERE t.organization_id = @OrganizationId AND t.archived_at IS NULL AND t.is_active = 1
  AND (
        (t.job_title_id IS NOT NULL AND t.job_title_id = e.job_title_id)
        OR (t.department_id IS NOT NULL AND t.department_id = e.department_id)
        OR (d.name IS NOT NULL AND t.name = d.name)
        OR t.is_default = 1
      )
ORDER BY
    CASE
        WHEN t.job_title_id IS NOT NULL AND t.job_title_id = e.job_title_id THEN 0
        WHEN t.department_id IS NOT NULL AND t.department_id = e.department_id THEN 1
        WHEN d.name IS NOT NULL AND t.name = d.name THEN 2
        ELSE 3
    END,
    t.name;";

        return await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(sql,
            new { OrganizationId = organizationId, EmployeeId = employeeId }, tx, cancellationToken: cancellationToken));
    }

    private static async Task<OnboardingTemplateDetailDto?> GetTemplateCoreAsync(
        IDbConnection connection, IDbTransaction? tx, Guid organizationId, Guid templateId, CancellationToken cancellationToken)
    {
        var template = await connection.QuerySingleOrDefaultAsync<OnboardingTemplateDetailDto>(new CommandDefinition(
            TemplateSelect + " WHERE t.organization_id = @OrganizationId AND t.id = @Id;",
            new { OrganizationId = organizationId, Id = templateId }, tx, cancellationToken: cancellationToken));
        if (template == null) return null;

        template.Tasks = (await connection.QueryAsync<OnboardingTemplateTaskDto>(new CommandDefinition(@"
SELECT id AS Id, title AS Title, description AS Description, category AS Category, display_order AS DisplayOrder,
       assignment_type AS AssignmentType, default_assignee_user_id AS DefaultAssigneeUserId,
       due_offset_days AS DueOffsetDays, is_required AS IsRequired, requires_document AS RequiresDocument,
       document_category AS DocumentCategory, requires_approval AS RequiresApproval, depends_on_task_id AS DependsOnTaskId
FROM dbo.nh_hr_onboarding_template_tasks
WHERE organization_id = @OrganizationId AND template_id = @Id
ORDER BY display_order, created_at;",
            new { OrganizationId = organizationId, Id = templateId }, tx, cancellationToken: cancellationToken))).ToList();
        return template;
    }

    private static async Task<Guid> InsertTemplateCoreAsync(
        IDbConnection connection, IDbTransaction tx, Guid organizationId,
        UpsertOnboardingTemplateRequest request, Guid? actorUserId, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        if (request.IsDefault)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.nh_hr_onboarding_templates SET is_default = 0 WHERE organization_id = @OrganizationId AND is_default = 1;",
                new { OrganizationId = organizationId }, tx, cancellationToken: cancellationToken));
        }

        await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_onboarding_templates
    (id, organization_id, name, description, department_id, job_title_id, employment_type_id,
     estimated_duration_days, is_active, is_default, version, created_at, created_by)
VALUES
    (@Id, @OrganizationId, @Name, @Description, @DepartmentId, @JobTitleId, @EmploymentTypeId,
     @EstimatedDurationDays, @IsActive, @IsDefault, 1, @Now, @ActorUserId);",
            new
            {
                Id = id, OrganizationId = organizationId,
                Name = request.Name.Trim(), Description = Trim(request.Description),
                request.DepartmentId, request.JobTitleId, request.EmploymentTypeId, request.EstimatedDurationDays,
                request.IsActive, request.IsDefault, Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId
            }, tx, cancellationToken: cancellationToken));

        await InsertTemplateTasksCoreAsync(connection, tx, organizationId, id, request.Tasks, cancellationToken);
        return id;
    }

    /// <summary>Inserts tasks with fresh ids, remapping dependencies expressed through the payload's client ids.</summary>
    private static async Task InsertTemplateTasksCoreAsync(
        IDbConnection connection, IDbTransaction tx, Guid organizationId, Guid templateId,
        IReadOnlyList<UpsertOnboardingTemplateTaskRequest>? tasks, CancellationToken cancellationToken)
    {
        if (tasks == null || tasks.Count == 0) return;

        var idMap = new Dictionary<Guid, Guid>();
        var newIds = new List<Guid>();
        foreach (var task in tasks)
        {
            var newId = Guid.NewGuid();
            newIds.Add(newId);
            if (task.Id.HasValue && task.Id.Value != Guid.Empty) idMap[task.Id.Value] = newId;
        }

        const string sql = @"
INSERT INTO dbo.nh_hr_onboarding_template_tasks
    (id, organization_id, template_id, title, description, category, display_order, assignment_type,
     default_assignee_user_id, due_offset_days, is_required, requires_document, document_category,
     requires_approval, depends_on_task_id, created_at)
VALUES
    (@Id, @OrganizationId, @TemplateId, @Title, @Description, @Category, @DisplayOrder, @AssignmentType,
     @DefaultAssigneeUserId, @DueOffsetDays, @IsRequired, @RequiresDocument, @DocumentCategory,
     @RequiresApproval, @DependsOnTaskId, @Now);";

        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < tasks.Count; i++)
        {
            var t = tasks[i];
            Guid? dependsOn = t.DependsOnTaskId.HasValue && idMap.TryGetValue(t.DependsOnTaskId.Value, out var mapped) ? mapped : null;
            await connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                Id = newIds[i], OrganizationId = organizationId, TemplateId = templateId,
                Title = t.Title.Trim(), Description = Trim(t.Description),
                Category = OnboardingValidation.NormalizeCategory(t.Category) ?? OnboardingTaskCategories.Other,
                DisplayOrder = i + 1,
                AssignmentType = OnboardingValidation.NormalizeAssignmentType(t.AssignmentType) ?? OnboardingAssignmentTypes.HR,
                t.DefaultAssigneeUserId, t.DueOffsetDays, t.IsRequired, t.RequiresDocument,
                DocumentCategory = Trim(t.DocumentCategory),
                t.RequiresApproval, DependsOnTaskId = dependsOn, Now = now
            }, tx, cancellationToken: cancellationToken));
        }
    }

    // -------------------------------------------------------------------------
    // Onboardings: read
    // -------------------------------------------------------------------------

    public async Task<PagedResult<OnboardingListItemDto>> ListAsync(Guid organizationId, OnboardingListQuery query, CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var today = DateTime.UtcNow.Date;

        var args = new
        {
            OrganizationId = organizationId,
            Status = string.IsNullOrWhiteSpace(query.Status) ? null : query.Status.Trim(),
            query.DepartmentId,
            query.OwnerUserId,
            query.TemplateId,
            StartFrom = query.StartFrom?.Date,
            StartTo = query.StartTo?.Date,
            OverdueOnly = query.OverdueOnly ? 1 : 0,
            Today = today,
            SearchLike = string.IsNullOrWhiteSpace(query.Search) ? null : "%" + EscapeLike(query.Search.Trim()) + "%",
            Offset = (page - 1) * pageSize,
            PageSize = pageSize
        };

        var countSql = "SELECT COUNT(1) " + OnboardingJoins + OnboardingFilters + ";";
        var pageSql = OnboardingSelect + OnboardingJoins + OnboardingFilters + $@"
ORDER BY {BuildOrderBy(query.Sort, query.Dir)}, o.id
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";

        using var connection = OpenConnection();
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(countSql, args, cancellationToken: cancellationToken));
        var items = (await connection.QueryAsync<OnboardingListItemDto>(new CommandDefinition(pageSql, args, cancellationToken: cancellationToken))).ToList();
        await AttachProgressAsync(connection, organizationId, items, today, cancellationToken);

        return new PagedResult<OnboardingListItemDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            Total = total
        };
    }

    public async Task<OnboardingPipelineDto> PipelineAsync(Guid organizationId, OnboardingPipelineQuery query, CancellationToken cancellationToken = default)
    {
        const int cap = 1000;
        var today = DateTime.UtcNow.Date;
        var completedSince = today.AddDays(-Math.Clamp(query.CompletedWithinDays, 1, 3650));

        var args = new
        {
            OrganizationId = organizationId,
            Status = (string?)null,
            query.DepartmentId,
            query.OwnerUserId,
            query.TemplateId,
            StartFrom = query.StartFrom?.Date,
            StartTo = query.StartTo?.Date,
            OverdueOnly = query.OverdueOnly ? 1 : 0,
            Today = today,
            SearchLike = string.IsNullOrWhiteSpace(query.Search) ? null : "%" + EscapeLike(query.Search.Trim()) + "%",
            CompletedSince = completedSince,
            Cap = cap
        };

        // Cancelled onboardings never appear on the board.
        var sql = "SELECT TOP (@Cap) * FROM (" + OnboardingSelect + OnboardingJoins + OnboardingFilters + @"
  AND o.status IN ('NotStarted', 'InProgress', 'InReview', 'Completed')
  AND (o.status <> 'Completed' OR o.actual_completion_date IS NULL OR o.actual_completion_date >= @CompletedSince)
) AS p
ORDER BY CASE WHEN p.ExpectedCompletionDate IS NULL THEN 1 ELSE 0 END, p.ExpectedCompletionDate, p.EmployeeName, p.Id;";

        using var connection = OpenConnection();
        var items = (await connection.QueryAsync<OnboardingListItemDto>(new CommandDefinition(sql, args, cancellationToken: cancellationToken))).ToList();
        await AttachProgressAsync(connection, organizationId, items, today, cancellationToken);

        var pipeline = new OnboardingPipelineDto();
        foreach (var (statusId, label) in OnboardingStatuses.PipelineColumns)
        {
            var cards = items.Where(i => i.Status.Equals(statusId, StringComparison.OrdinalIgnoreCase))
                .Select(i => new OnboardingPipelineCardDto
                {
                    Id = i.Id,
                    EmployeeId = i.EmployeeId,
                    EmployeeName = i.EmployeeName,
                    JobTitle = i.JobTitle,
                    Department = i.Department,
                    AvatarUrl = i.AvatarUrl,
                    StartDate = i.StartDate,
                    ExpectedCompletion = i.ExpectedCompletionDate,
                    ProgressPercent = i.ProgressPercent,
                    OverdueCount = i.OverdueCount,
                    OwnerName = i.OwnerName,
                    TemplateName = i.TemplateName
                }).ToList();
            pipeline.Columns.Add(new OnboardingPipelineColumnDto { Status = statusId, Label = label, Count = cards.Count, Cards = cards });
        }
        return pipeline;
    }

    public async Task<OnboardingDetailDto?> GetAsync(Guid organizationId, Guid onboardingId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var detail = await connection.QuerySingleOrDefaultAsync<OnboardingDetailDto>(new CommandDefinition(
            OnboardingSelect + OnboardingJoins + " WHERE o.organization_id = @OrganizationId AND o.id = @Id;",
            new { OrganizationId = organizationId, Id = onboardingId }, cancellationToken: cancellationToken));
        if (detail == null) return null;

        var today = DateTime.UtcNow.Date;
        var tasks = await ListTasksCoreAsync(connection, organizationId, onboardingId, today, cancellationToken);
        detail.Tasks = tasks.ToList();
        detail.Documents = (await connection.QueryAsync<OnboardingDocumentDto>(new CommandDefinition(
            DocumentSelect + " WHERE organization_id = @OrganizationId AND onboarding_id = @Id ORDER BY created_at, name;",
            new { OrganizationId = organizationId, Id = onboardingId }, cancellationToken: cancellationToken))).ToList();

        ApplyProgress(detail, tasks);
        var blockers = OnboardingValidation.GetCompletionBlockers(tasks);
        detail.CompletionBlockers = blockers.Select(t => t.Title).ToList();
        detail.CanComplete = blockers.Count == 0;
        return detail;
    }

    public async Task<OnboardingHeader?> GetHeaderAsync(Guid organizationId, Guid onboardingId, CancellationToken cancellationToken = default)
    {
        var sql = @"
SELECT o.id AS Id, o.employee_id AS EmployeeId, " + EmployeeNameSql + @" AS EmployeeName, o.status AS Status,
       o.start_date AS StartDate, o.expected_completion_date AS ExpectedCompletionDate,
       o.owner_user_id AS OwnerUserId, o.template_id AS TemplateId
FROM dbo.nh_hr_employee_onboardings o
INNER JOIN dbo.nh_hr_employees e ON e.id = o.employee_id AND e.organization_id = o.organization_id
WHERE o.organization_id = @OrganizationId AND o.id = @Id;";

        using var connection = OpenConnection();
        return await connection.QuerySingleOrDefaultAsync<OnboardingHeader>(new CommandDefinition(sql,
            new { OrganizationId = organizationId, Id = onboardingId }, cancellationToken: cancellationToken));
    }

    public async Task<OnboardingEmployeeInfo?> GetEmployeeInfoAsync(Guid organizationId, Guid employeeId, CancellationToken cancellationToken = default)
    {
        var sql = @"
SELECT e.id AS Id, " + EmployeeNameSql + @" AS DisplayName,
       CAST(CASE WHEN e.archived_at IS NULL THEN 0 ELSE 1 END AS bit) AS IsArchived,
       e.application_id AS ApplicationId, e.hire_date AS HireDate
FROM dbo.nh_hr_employees e
WHERE e.organization_id = @OrganizationId AND e.id = @EmployeeId;";

        using var connection = OpenConnection();
        return await connection.QuerySingleOrDefaultAsync<OnboardingEmployeeInfo>(new CommandDefinition(sql,
            new { OrganizationId = organizationId, EmployeeId = employeeId }, cancellationToken: cancellationToken));
    }

    public async Task<Guid?> FindActiveOnboardingIdAsync(Guid organizationId, Guid employeeId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT TOP 1 id FROM dbo.nh_hr_employee_onboardings
WHERE organization_id = @OrganizationId AND employee_id = @EmployeeId
  AND status IN ('NotStarted', 'InProgress', 'InReview');";

        using var connection = OpenConnection();
        return await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(sql,
            new { OrganizationId = organizationId, EmployeeId = employeeId }, cancellationToken: cancellationToken));
    }

    public async Task<bool> OrgUserExistsAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.nh_users WHERE org_id = @OrganizationId AND id = @UserId) THEN 1 ELSE 0 END;";
        using var connection = OpenConnection();
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql,
            new { OrganizationId = organizationId, UserId = userId }, cancellationToken: cancellationToken)) == 1;
    }

    // -------------------------------------------------------------------------
    // Onboardings: create / update / transitions
    // -------------------------------------------------------------------------

    public async Task<CreatedOnboarding> CreateOnboardingAsync(Guid organizationId, CreateOnboardingCommand command, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        try
        {
            var created = await InsertOnboardingCoreAsync(connection, tx, organizationId, command, cancellationToken);
            tx.Commit();
            return created;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private sealed class EmployeeLinkRow
    {
        public Guid? LinkedUserId { get; set; }
        public Guid? ManagerUserId { get; set; }
    }

    private sealed class TemplateRow
    {
        public Guid Id { get; set; }
        public int Version { get; set; }
        public int? EstimatedDurationDays { get; set; }
    }

    private sealed class TemplateTaskRow
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string Category { get; set; } = OnboardingTaskCategories.Other;
        public int DisplayOrder { get; set; }
        public string AssignmentType { get; set; } = OnboardingAssignmentTypes.HR;
        public Guid? DefaultAssigneeUserId { get; set; }
        public int? DueOffsetDays { get; set; }
        public bool IsRequired { get; set; }
        public bool RequiresDocument { get; set; }
        public string? DocumentCategory { get; set; }
        public bool RequiresApproval { get; set; }
        public Guid? DependsOnTaskId { get; set; }
    }

    private async Task<CreatedOnboarding> InsertOnboardingCoreAsync(
        IDbConnection connection, IDbTransaction tx, Guid organizationId, CreateOnboardingCommand cmd, CancellationToken cancellationToken)
    {
        var employeeLinks = await connection.QuerySingleOrDefaultAsync<EmployeeLinkRow>(new CommandDefinition(@"
SELECT e.linked_user_id AS LinkedUserId, m.linked_user_id AS ManagerUserId
FROM dbo.nh_hr_employees e
LEFT JOIN dbo.nh_hr_employees m ON m.id = e.manager_id AND m.organization_id = e.organization_id
WHERE e.organization_id = @OrganizationId AND e.id = @EmployeeId AND e.archived_at IS NULL;",
            new { OrganizationId = organizationId, cmd.EmployeeId }, tx, cancellationToken: cancellationToken))
            ?? throw new InvalidOperationException("Employee not found.");

        TemplateRow? template = null;
        var templateTasks = new List<TemplateTaskRow>();
        if (cmd.TemplateId.HasValue)
        {
            template = await connection.QuerySingleOrDefaultAsync<TemplateRow>(new CommandDefinition(@"
SELECT id AS Id, version AS Version, estimated_duration_days AS EstimatedDurationDays
FROM dbo.nh_hr_onboarding_templates
WHERE organization_id = @OrganizationId AND id = @Id AND archived_at IS NULL;",
                new { OrganizationId = organizationId, Id = cmd.TemplateId.Value }, tx, cancellationToken: cancellationToken))
                ?? throw new InvalidOperationException("Template not found.");

            templateTasks = (await connection.QueryAsync<TemplateTaskRow>(new CommandDefinition(@"
SELECT id AS Id, title AS Title, description AS Description, category AS Category, display_order AS DisplayOrder,
       assignment_type AS AssignmentType, default_assignee_user_id AS DefaultAssigneeUserId,
       due_offset_days AS DueOffsetDays, is_required AS IsRequired, requires_document AS RequiresDocument,
       document_category AS DocumentCategory, requires_approval AS RequiresApproval, depends_on_task_id AS DependsOnTaskId
FROM dbo.nh_hr_onboarding_template_tasks
WHERE organization_id = @OrganizationId AND template_id = @Id
ORDER BY display_order, created_at;",
                new { OrganizationId = organizationId, Id = cmd.TemplateId.Value }, tx, cancellationToken: cancellationToken))).ToList();
        }

        var excluded = new HashSet<Guid>(cmd.ExcludedTemplateTaskIds);
        var selected = templateTasks.Where(t => !excluded.Contains(t.Id)).ToList();

        var onboardingId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var start = cmd.StartDate?.Date;
        var expected = cmd.ExpectedCompletionDate?.Date
                       ?? (start.HasValue && template?.EstimatedDurationDays != null ? start.Value.AddDays(template.EstimatedDurationDays.Value) : null);

        await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_employee_onboardings
    (id, organization_id, employee_id, template_id, template_version, owner_user_id, start_date,
     expected_completion_date, status, notes, created_at, created_by)
VALUES
    (@Id, @OrganizationId, @EmployeeId, @TemplateId, @TemplateVersion, @OwnerUserId, @StartDate,
     @ExpectedCompletionDate, @Status, @Notes, @Now, @ActorUserId);",
            new
            {
                Id = onboardingId, OrganizationId = organizationId, cmd.EmployeeId,
                TemplateId = template?.Id, TemplateVersion = template?.Version,
                cmd.OwnerUserId, StartDate = start, ExpectedCompletionDate = expected,
                Status = cmd.InitialStatus, Notes = Trim(cmd.Notes), Now = now, ActorUserId = cmd.ActorUserId
            }, tx, cancellationToken: cancellationToken));

        // Snapshot template tasks (due date = start date + offset), remapping dependencies.
        var idMap = selected.ToDictionary(t => t.Id, _ => Guid.NewGuid());
        var taskIds = new List<Guid>();
        var order = 0;
        foreach (var tt in selected)
        {
            order++;
            Guid? assigned = tt.AssignmentType switch
            {
                OnboardingAssignmentTypes.SpecificUser => tt.DefaultAssigneeUserId,
                OnboardingAssignmentTypes.HR => tt.DefaultAssigneeUserId ?? cmd.OwnerUserId,
                OnboardingAssignmentTypes.IT => tt.DefaultAssigneeUserId,
                OnboardingAssignmentTypes.DirectManager => employeeLinks.ManagerUserId ?? tt.DefaultAssigneeUserId,
                OnboardingAssignmentTypes.Employee => employeeLinks.LinkedUserId,
                _ => tt.DefaultAssigneeUserId
            };

            var taskId = idMap[tt.Id];
            taskIds.Add(taskId);
            await InsertTaskCoreAsync(connection, tx, organizationId, onboardingId, taskId, new OnboardingTaskSpec
            {
                Title = tt.Title,
                Description = tt.Description,
                Category = tt.Category,
                AssignmentType = tt.AssignmentType,
                AssignedUserId = assigned,
                DueDate = OnboardingValidation.ComputeDueDate(start, tt.DueOffsetDays),
                IsRequired = tt.IsRequired,
                RequiresDocument = tt.RequiresDocument,
                DocumentCategory = tt.DocumentCategory,
                RequiresApproval = tt.RequiresApproval,
                DependsOnTaskId = tt.DependsOnTaskId.HasValue && idMap.TryGetValue(tt.DependsOnTaskId.Value, out var dep) ? dep : null
            }, tt.Id, order, now, cancellationToken);
        }

        foreach (var extra in cmd.AdditionalTasks)
        {
            order++;
            var taskId = Guid.NewGuid();
            taskIds.Add(taskId);
            await InsertTaskCoreAsync(connection, tx, organizationId, onboardingId, taskId, extra, null, order, now, cancellationToken);
        }

        await InsertActivityCoreAsync(connection, tx, organizationId, onboardingId, "Created", "Onboarding created",
            JsonSerializer.Serialize(new { templateId = template?.Id, taskCount = taskIds.Count }),
            cmd.ActorUserId, cmd.ActorName, cancellationToken);
        if (cmd.InitialStatus == OnboardingStatuses.InProgress)
        {
            await InsertActivityCoreAsync(connection, tx, organizationId, onboardingId, "StatusChanged",
                "Onboarding started", JsonSerializer.Serialize(new { from = OnboardingStatuses.NotStarted, to = OnboardingStatuses.InProgress }),
                cmd.ActorUserId, cmd.ActorName, cancellationToken);
        }

        return new CreatedOnboarding(onboardingId, taskIds);
    }

    private static async Task InsertTaskCoreAsync(
        IDbConnection connection, IDbTransaction tx, Guid organizationId, Guid onboardingId, Guid taskId,
        OnboardingTaskSpec spec, Guid? templateTaskId, int displayOrder, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var category = OnboardingValidation.NormalizeCategory(spec.Category) ?? OnboardingTaskCategories.Other;
        var assignment = OnboardingValidation.NormalizeAssignmentType(spec.AssignmentType) ?? OnboardingAssignmentTypes.HR;
        var docCategory = Trim(spec.DocumentCategory);

        await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_onboarding_tasks
    (id, organization_id, onboarding_id, template_task_id, title, description, category, assignment_type,
     assigned_user_id, due_date, status, is_required, requires_document, document_category, requires_approval,
     display_order, depends_on_task_id, is_waived, created_at)
VALUES
    (@Id, @OrganizationId, @OnboardingId, @TemplateTaskId, @Title, @Description, @Category, @AssignmentType,
     @AssignedUserId, @DueDate, 'Pending', @IsRequired, @RequiresDocument, @DocumentCategory, @RequiresApproval,
     @DisplayOrder, @DependsOnTaskId, 0, @Now);",
            new
            {
                Id = taskId, OrganizationId = organizationId, OnboardingId = onboardingId, TemplateTaskId = templateTaskId,
                Title = spec.Title.Trim(), Description = Trim(spec.Description), Category = category, AssignmentType = assignment,
                spec.AssignedUserId, DueDate = spec.DueDate?.Date, spec.IsRequired, spec.RequiresDocument,
                DocumentCategory = docCategory, spec.RequiresApproval, DisplayOrder = displayOrder,
                spec.DependsOnTaskId, Now = now
            }, tx, cancellationToken: cancellationToken));

        if (spec.RequiresDocument)
            await InsertDocumentRequirementCoreAsync(connection, tx, organizationId, onboardingId, taskId, spec, now, cancellationToken);
    }

    private static Task InsertDocumentRequirementCoreAsync(
        IDbConnection connection, IDbTransaction? tx, Guid organizationId, Guid onboardingId, Guid taskId,
        OnboardingTaskSpec spec, DateTimeOffset now, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_onboarding_documents
    (id, organization_id, onboarding_id, onboarding_task_id, category, name, is_required, status, created_at)
VALUES
    (@Id, @OrganizationId, @OnboardingId, @TaskId, @Category, @Name, @IsRequired, 'Missing', @Now);",
            new
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, OnboardingId = onboardingId, TaskId = taskId,
                Category = Trim(spec.DocumentCategory) ?? OnboardingTaskCategories.Other,
                Name = spec.Title.Trim().Length > 300 ? spec.Title.Trim()[..300] : spec.Title.Trim(),
                spec.IsRequired, Now = now
            }, tx, cancellationToken: cancellationToken));

    public async Task<bool> UpdateAsync(Guid organizationId, Guid onboardingId, UpdateOnboardingRequest request, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        try
        {
            var previousStart = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                "SELECT start_date FROM dbo.nh_hr_employee_onboardings WHERE organization_id = @OrganizationId AND id = @Id;",
                new { OrganizationId = organizationId, Id = onboardingId }, tx, cancellationToken: cancellationToken));

            var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_employee_onboardings
SET owner_user_id = @OwnerUserId, start_date = @StartDate, expected_completion_date = @ExpectedCompletionDate,
    notes = @Notes, updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND id = @Id;",
                new
                {
                    OrganizationId = organizationId, Id = onboardingId, request.OwnerUserId,
                    StartDate = request.StartDate?.Date, ExpectedCompletionDate = request.ExpectedCompletionDate?.Date,
                    Notes = Trim(request.Notes), Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId
                }, tx, cancellationToken: cancellationToken));
            if (affected == 0)
            {
                tx.Rollback();
                return false;
            }

            if (previousStart == null && request.StartDate.HasValue)
                await BackfillDueDatesAsync(connection, tx, organizationId, onboardingId, request.StartDate.Value.Date, cancellationToken);

            tx.Commit();
            return true;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<bool> TransitionStatusAsync(
        Guid organizationId, Guid onboardingId, string from, string to,
        DateTime? completionDate, string? cancelReason, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        try
        {
            var today = DateTime.UtcNow.Date;
            var hadStart = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                "SELECT start_date FROM dbo.nh_hr_employee_onboardings WHERE organization_id = @OrganizationId AND id = @Id;",
                new { OrganizationId = organizationId, Id = onboardingId }, tx, cancellationToken: cancellationToken));

            var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_employee_onboardings
SET status = @To,
    start_date = CASE WHEN @To = 'InProgress' AND start_date IS NULL THEN @Today ELSE start_date END,
    actual_completion_date = CASE WHEN @To = 'Completed' THEN @CompletionDate
                                  WHEN @To = 'InProgress' THEN NULL
                                  ELSE actual_completion_date END,
    cancel_reason = CASE WHEN @To = 'Cancelled' THEN @CancelReason ELSE cancel_reason END,
    updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND id = @Id AND status = @From;",
                new
                {
                    OrganizationId = organizationId, Id = onboardingId, From = from, To = to, Today = today,
                    CompletionDate = (completionDate ?? today).Date, CancelReason = Trim(cancelReason),
                    Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId
                }, tx, cancellationToken: cancellationToken));
            if (affected == 0)
            {
                tx.Rollback();
                return false;
            }

            if (to == OnboardingStatuses.InProgress && hadStart == null)
                await BackfillDueDatesAsync(connection, tx, organizationId, onboardingId, today, cancellationToken);

            tx.Commit();
            return true;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>When an onboarding gets its start date late, tasks that came from a template get start date + offset.</summary>
    private static Task BackfillDueDatesAsync(
        IDbConnection connection, IDbTransaction tx, Guid organizationId, Guid onboardingId, DateTime startDate, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(@"
UPDATE ot
SET ot.due_date = DATEADD(day, tt.due_offset_days, @StartDate), ot.updated_at = @Now
FROM dbo.nh_hr_onboarding_tasks ot
INNER JOIN dbo.nh_hr_onboarding_template_tasks tt ON tt.id = ot.template_task_id
WHERE ot.organization_id = @OrganizationId AND ot.onboarding_id = @OnboardingId
  AND ot.due_date IS NULL AND tt.due_offset_days IS NOT NULL;",
            new { OrganizationId = organizationId, OnboardingId = onboardingId, StartDate = startDate.Date, Now = DateTimeOffset.UtcNow },
            tx, cancellationToken: cancellationToken));

    // -------------------------------------------------------------------------
    // Tasks
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<OnboardingTaskDto>> ListTasksAsync(Guid organizationId, Guid onboardingId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        return await ListTasksCoreAsync(connection, organizationId, onboardingId, DateTime.UtcNow.Date, cancellationToken);
    }

    private static async Task<IReadOnlyList<OnboardingTaskDto>> ListTasksCoreAsync(
        IDbConnection connection, Guid organizationId, Guid onboardingId, DateTime today, CancellationToken cancellationToken)
    {
        var tasks = (await connection.QueryAsync<OnboardingTaskDto>(new CommandDefinition(
            TaskSelect + " WHERE t.organization_id = @OrganizationId AND t.onboarding_id = @OnboardingId ORDER BY t.display_order, t.created_at;",
            new { OrganizationId = organizationId, OnboardingId = onboardingId }, cancellationToken: cancellationToken))).ToList();
        foreach (var t in tasks) t.IsOverdue = OnboardingValidation.IsOverdue(t, today);
        return tasks;
    }

    public async Task<OnboardingTaskDto?> GetTaskAsync(Guid organizationId, Guid onboardingId, Guid taskId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var task = await connection.QuerySingleOrDefaultAsync<OnboardingTaskDto>(new CommandDefinition(
            TaskSelect + " WHERE t.organization_id = @OrganizationId AND t.onboarding_id = @OnboardingId AND t.id = @Id;",
            new { OrganizationId = organizationId, OnboardingId = onboardingId, Id = taskId }, cancellationToken: cancellationToken));
        if (task != null) task.IsOverdue = OnboardingValidation.IsOverdue(task, DateTime.UtcNow.Date);
        return task;
    }

    public async Task<Guid> AddTaskAsync(Guid organizationId, Guid onboardingId, OnboardingTaskSpec task, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        try
        {
            var order = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT ISNULL(MAX(display_order), 0) + 1 FROM dbo.nh_hr_onboarding_tasks WHERE organization_id = @OrganizationId AND onboarding_id = @OnboardingId;",
                new { OrganizationId = organizationId, OnboardingId = onboardingId }, tx, cancellationToken: cancellationToken));
            var id = Guid.NewGuid();
            await InsertTaskCoreAsync(connection, tx, organizationId, onboardingId, id, task, null, order, DateTimeOffset.UtcNow, cancellationToken);
            tx.Commit();
            return id;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<bool> UpdateTaskAsync(
        Guid organizationId, Guid onboardingId, Guid taskId, OnboardingTaskSpec task, string? status, bool? isWaived, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var now = DateTimeOffset.UtcNow;
        var category = OnboardingValidation.NormalizeCategory(task.Category) ?? OnboardingTaskCategories.Other;
        var assignment = OnboardingValidation.NormalizeAssignmentType(task.AssignmentType) ?? OnboardingAssignmentTypes.HR;

        var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_onboarding_tasks
SET title = @Title, description = @Description, category = @Category, assignment_type = @AssignmentType,
    assigned_user_id = @AssignedUserId, due_date = @DueDate, is_required = @IsRequired,
    requires_document = @RequiresDocument, document_category = @DocumentCategory,
    requires_approval = @RequiresApproval,
    status = CASE WHEN @Status IS NOT NULL AND status NOT IN ('Completed', 'Cancelled') THEN @Status ELSE status END,
    is_waived = ISNULL(@IsWaived, is_waived),
    updated_at = @Now
WHERE organization_id = @OrganizationId AND onboarding_id = @OnboardingId AND id = @Id AND status <> 'Cancelled';",
            new
            {
                OrganizationId = organizationId, OnboardingId = onboardingId, Id = taskId,
                Title = task.Title.Trim(), Description = Trim(task.Description), Category = category, AssignmentType = assignment,
                task.AssignedUserId, DueDate = task.DueDate?.Date, task.IsRequired, task.RequiresDocument,
                DocumentCategory = Trim(task.DocumentCategory), task.RequiresApproval,
                Status = status, IsWaived = isWaived, Now = now
            }, cancellationToken: cancellationToken));
        if (affected == 0) return false;

        if (task.RequiresDocument)
        {
            var hasDoc = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.nh_hr_onboarding_documents WHERE organization_id = @OrganizationId AND onboarding_task_id = @TaskId) THEN 1 ELSE 0 END;",
                new { OrganizationId = organizationId, TaskId = taskId }, cancellationToken: cancellationToken)) == 1;
            if (!hasDoc)
                await InsertDocumentRequirementCoreAsync(connection, null, organizationId, onboardingId, taskId, task, now, cancellationToken);
        }

        // Keep the mirrored ATS task (if any) in sync; failures never block onboarding.
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE t
SET t.title = @Title, t.due_at = @DueAt, t.assigned_to_user_id = @AssignedUserId
FROM dbo.tasks t
INNER JOIN dbo.nh_hr_onboarding_tasks ot ON ot.ats_task_id = t.id
WHERE ot.organization_id = @OrganizationId AND ot.id = @TaskId;",
                new
                {
                    OrganizationId = organizationId, TaskId = taskId, Title = task.Title.Trim(),
                    DueAt = ToDueAt(task.DueDate), task.AssignedUserId
                }, cancellationToken: cancellationToken));
        }
        catch (SqlException ex)
        {
            _logger.LogDebug(ex, "Could not sync ATS task for onboarding task {TaskId}.", taskId);
        }
        return true;
    }

    public async Task<bool> CancelTaskAsync(Guid organizationId, Guid onboardingId, Guid taskId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_onboarding_tasks SET status = 'Cancelled', updated_at = @Now
WHERE organization_id = @OrganizationId AND onboarding_id = @OnboardingId AND id = @Id AND status <> 'Cancelled';",
            new { OrganizationId = organizationId, OnboardingId = onboardingId, Id = taskId, Now = DateTimeOffset.UtcNow },
            cancellationToken: cancellationToken));
        if (affected > 0) await SetAtsTaskStatusAsync(connection, organizationId, taskId, "done", cancellationToken);
        return affected > 0;
    }

    public async Task<bool> CompleteTaskAsync(Guid organizationId, Guid onboardingId, Guid taskId, string? notes, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_onboarding_tasks
SET status = 'Completed', completed_at = @Now, completed_by = @ActorUserId, completion_notes = @Notes,
    approval_status = CASE WHEN requires_approval = 1 THEN 'Pending' ELSE NULL END,
    approved_by = NULL, approved_at = NULL, updated_at = @Now
WHERE organization_id = @OrganizationId AND onboarding_id = @OnboardingId AND id = @Id
  AND status NOT IN ('Completed', 'Cancelled');",
            new
            {
                OrganizationId = organizationId, OnboardingId = onboardingId, Id = taskId,
                Notes = Trim(notes), Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId
            }, cancellationToken: cancellationToken));
        if (affected > 0) await SetAtsTaskStatusAsync(connection, organizationId, taskId, "done", cancellationToken);
        return affected > 0;
    }

    public async Task<bool> ReopenTaskAsync(Guid organizationId, Guid onboardingId, Guid taskId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_onboarding_tasks
SET status = 'InProgress', completed_at = NULL, completed_by = NULL, completion_notes = NULL,
    approval_status = NULL, approved_by = NULL, approved_at = NULL, updated_at = @Now
WHERE organization_id = @OrganizationId AND onboarding_id = @OnboardingId AND id = @Id AND status = 'Completed';",
            new { OrganizationId = organizationId, OnboardingId = onboardingId, Id = taskId, Now = DateTimeOffset.UtcNow },
            cancellationToken: cancellationToken));
        if (affected > 0) await SetAtsTaskStatusAsync(connection, organizationId, taskId, "todo", cancellationToken);
        return affected > 0;
    }

    public async Task<bool> ApproveTaskAsync(Guid organizationId, Guid onboardingId, Guid taskId, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_onboarding_tasks
SET approval_status = 'Approved', approved_by = @ActorUserId, approved_at = @Now, updated_at = @Now
WHERE organization_id = @OrganizationId AND onboarding_id = @OnboardingId AND id = @Id
  AND status = 'Completed' AND requires_approval = 1 AND approval_status = 'Pending';",
            new { OrganizationId = organizationId, OnboardingId = onboardingId, Id = taskId, Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId },
            cancellationToken: cancellationToken));
        return affected > 0;
    }

    public async Task<bool> RejectTaskAsync(Guid organizationId, Guid onboardingId, Guid taskId, string reason, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_onboarding_tasks
SET approval_status = 'Rejected', approved_by = @ActorUserId, approved_at = @Now,
    status = 'InProgress', completed_at = NULL, completed_by = NULL, completion_notes = @Reason, updated_at = @Now
WHERE organization_id = @OrganizationId AND onboarding_id = @OnboardingId AND id = @Id
  AND status = 'Completed' AND requires_approval = 1 AND approval_status = 'Pending';",
            new
            {
                OrganizationId = organizationId, OnboardingId = onboardingId, Id = taskId,
                Reason = Trim(reason), Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId
            }, cancellationToken: cancellationToken));
        if (affected > 0) await SetAtsTaskStatusAsync(connection, organizationId, taskId, "todo", cancellationToken);
        return affected > 0;
    }

    private sealed class AtsLinkRow
    {
        public Guid TaskId { get; set; }
        public string Title { get; set; } = string.Empty;
        public Guid AssignedUserId { get; set; }
        public DateTime? DueDate { get; set; }
        public Guid? ApplicationId { get; set; }
    }

    public async Task LinkAtsTasksAsync(Guid organizationId, Guid onboardingId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = OpenConnection();
            var rows = (await connection.QueryAsync<AtsLinkRow>(new CommandDefinition(@"
SELECT ot.id AS TaskId, ot.title AS Title, ot.assigned_user_id AS AssignedUserId, ot.due_date AS DueDate,
       e.application_id AS ApplicationId
FROM dbo.nh_hr_onboarding_tasks ot
INNER JOIN dbo.nh_hr_employee_onboardings o ON o.id = ot.onboarding_id AND o.organization_id = ot.organization_id
INNER JOIN dbo.nh_hr_employees e ON e.id = o.employee_id AND e.organization_id = o.organization_id
WHERE ot.organization_id = @OrganizationId AND ot.onboarding_id = @OnboardingId
  AND ot.assigned_user_id IS NOT NULL AND ot.ats_task_id IS NULL
  AND ot.status NOT IN ('Completed', 'Cancelled') AND ot.is_waived = 0;",
                new { OrganizationId = organizationId, OnboardingId = onboardingId }, cancellationToken: cancellationToken))).ToList();

            foreach (var row in rows)
            {
                var atsId = Guid.NewGuid();
                try
                {
                    // application_id may be NOT NULL in some deployments; the insert then fails and the link is skipped.
                    await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.tasks (id, application_id, assigned_to_user_id, title, due_at, status, created_at, source_type, source_id)
VALUES (@Id, @ApplicationId, @AssignedUserId, @Title, @DueAt, 'todo', @Now, 'onboarding', @SourceId);",
                        new
                        {
                            Id = atsId, row.ApplicationId, row.AssignedUserId, Title = row.Title,
                            DueAt = ToDueAt(row.DueDate), Now = DateTimeOffset.UtcNow, SourceId = row.TaskId
                        }, cancellationToken: cancellationToken));
                }
                catch (SqlException ex)
                {
                    _logger.LogDebug(ex, "Skipping ATS task link for onboarding task {TaskId}.", row.TaskId);
                    continue;
                }

                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE dbo.nh_hr_onboarding_tasks SET ats_task_id = @AtsId WHERE organization_id = @OrganizationId AND id = @TaskId;",
                    new { AtsId = atsId, OrganizationId = organizationId, TaskId = row.TaskId }, cancellationToken: cancellationToken));
            }
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "ATS task linking failed for onboarding {OnboardingId}.", onboardingId);
        }
    }

    private async Task SetAtsTaskStatusAsync(IDbConnection connection, Guid organizationId, Guid taskId, string atsStatus, CancellationToken cancellationToken)
    {
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE t
SET t.status = @Status,
    t.completed_at = CASE WHEN @Status = 'done' THEN @Now ELSE NULL END
FROM dbo.tasks t
INNER JOIN dbo.nh_hr_onboarding_tasks ot ON ot.ats_task_id = t.id
WHERE ot.organization_id = @OrganizationId AND ot.id = @TaskId;",
                new { Status = atsStatus, Now = DateTimeOffset.UtcNow, OrganizationId = organizationId, TaskId = taskId },
                cancellationToken: cancellationToken));
        }
        catch (SqlException ex)
        {
            _logger.LogDebug(ex, "Could not update ATS task status for onboarding task {TaskId}.", taskId);
        }
    }

    private static DateTimeOffset? ToDueAt(DateTime? dueDate) =>
        dueDate.HasValue ? new DateTimeOffset(dueDate.Value.Date, TimeSpan.Zero) : null;

    // -------------------------------------------------------------------------
    // Documents
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<OnboardingDocumentDto>> ListDocumentsAsync(Guid organizationId, Guid onboardingId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        return (await connection.QueryAsync<OnboardingDocumentDto>(new CommandDefinition(
            DocumentSelect + " WHERE organization_id = @OrganizationId AND onboarding_id = @OnboardingId ORDER BY created_at, name;",
            new { OrganizationId = organizationId, OnboardingId = onboardingId }, cancellationToken: cancellationToken))).ToList();
    }

    public async Task<OnboardingDocumentDto?> GetDocumentAsync(Guid organizationId, Guid onboardingId, Guid documentId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        return await connection.QuerySingleOrDefaultAsync<OnboardingDocumentDto>(new CommandDefinition(
            DocumentSelect + " WHERE organization_id = @OrganizationId AND onboarding_id = @OnboardingId AND id = @Id;",
            new { OrganizationId = organizationId, OnboardingId = onboardingId, Id = documentId }, cancellationToken: cancellationToken));
    }

    public async Task<bool> UpdateDocumentAsync(Guid organizationId, Guid onboardingId, Guid documentId, string status, Guid? employeeDocumentId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_onboarding_documents
SET status = @Status, employee_document_id = ISNULL(@EmployeeDocumentId, employee_document_id)
WHERE organization_id = @OrganizationId AND onboarding_id = @OnboardingId AND id = @Id;",
            new { OrganizationId = organizationId, OnboardingId = onboardingId, Id = documentId, Status = status, EmployeeDocumentId = employeeDocumentId },
            cancellationToken: cancellationToken));
        return affected > 0;
    }

    // -------------------------------------------------------------------------
    // Activity
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<OnboardingActivityDto>> ListActivityAsync(Guid organizationId, Guid onboardingId, int take, CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT TOP (@Take) id AS Id, onboarding_id AS OnboardingId, activity_type AS ActivityType, summary AS Summary,
       details_json AS DetailsJson, created_at AS CreatedAt, created_by AS CreatedBy, created_by_name AS CreatedByName
FROM dbo.nh_hr_onboarding_activity
WHERE organization_id = @OrganizationId AND onboarding_id = @OnboardingId
ORDER BY created_at DESC;";

        using var connection = OpenConnection();
        return (await connection.QueryAsync<OnboardingActivityDto>(new CommandDefinition(sql,
            new { OrganizationId = organizationId, OnboardingId = onboardingId, Take = Math.Clamp(take, 1, 500) },
            cancellationToken: cancellationToken))).ToList();
    }

    public async Task AddActivityAsync(
        Guid organizationId, Guid onboardingId, string activityType, string summary, string? detailsJson,
        Guid? actorUserId, string? actorName, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        await InsertActivityCoreAsync(connection, null, organizationId, onboardingId, activityType, summary, detailsJson, actorUserId, actorName, cancellationToken);
    }

    private static Task InsertActivityCoreAsync(
        IDbConnection connection, IDbTransaction? tx, Guid organizationId, Guid onboardingId,
        string activityType, string summary, string? detailsJson, Guid? actorUserId, string? actorName, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_onboarding_activity
    (id, organization_id, onboarding_id, activity_type, summary, details_json, created_at, created_by, created_by_name)
VALUES
    (@Id, @OrganizationId, @OnboardingId, @ActivityType, @Summary, @DetailsJson, @Now, @ActorUserId, @ActorName);",
            new
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, OnboardingId = onboardingId, ActivityType = activityType,
                Summary = summary.Length > 500 ? summary[..500] : summary, DetailsJson = Trim(detailsJson),
                Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId, ActorName = Trim(actorName)
            }, tx, cancellationToken: cancellationToken));

    // -------------------------------------------------------------------------
    // Summary
    // -------------------------------------------------------------------------

    private sealed class KeyCount
    {
        public string Key { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    public async Task<OnboardingSummaryDto> GetSummaryAsync(Guid organizationId, DateTime? from, DateTime? to, CancellationToken cancellationToken = default)
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

        var summary = new OnboardingSummaryDto { PeriodStart = periodStart, PeriodEnd = periodEnd };

        var byStatus = await connection.QueryAsync<KeyCount>(new CommandDefinition(@"
SELECT o.status AS [Key], COUNT(1) AS [Count]
FROM dbo.nh_hr_employee_onboardings o
WHERE o.organization_id = @OrganizationId
GROUP BY o.status;", org, cancellationToken: cancellationToken));
        summary.ByStatus = OnboardingStatuses.All.ToDictionary(s => s, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var row in byStatus) summary.ByStatus[row.Key] = row.Count;
        summary.NotStarted = summary.ByStatus.GetValueOrDefault(OnboardingStatuses.NotStarted);
        summary.InProgress = summary.ByStatus.GetValueOrDefault(OnboardingStatuses.InProgress);
        summary.InReview = summary.ByStatus.GetValueOrDefault(OnboardingStatuses.InReview);
        summary.ActiveOnboardings = summary.NotStarted + summary.InProgress + summary.InReview;

        var byDept = await connection.QueryAsync<KeyCount>(new CommandDefinition(@"
SELECT COALESCE(d.name, 'Unassigned') AS [Key], COUNT(1) AS [Count]
FROM dbo.nh_hr_employee_onboardings o
INNER JOIN dbo.nh_hr_employees e ON e.id = o.employee_id AND e.organization_id = o.organization_id
LEFT JOIN dbo.nh_hr_departments d ON d.id = e.department_id AND d.organization_id = e.organization_id
WHERE o.organization_id = @OrganizationId AND o.status IN ('NotStarted', 'InProgress', 'InReview')
GROUP BY COALESCE(d.name, 'Unassigned');", org, cancellationToken: cancellationToken));
        summary.ByDepartment = byDept.ToDictionary(r => r.Key, r => r.Count);

        summary.StartedInPeriod = await CountStartedAsync(connection, organizationId, periodStart, periodEnd, cancellationToken);
        summary.PreviousStartedInPeriod = await CountStartedAsync(connection, organizationId, prevStart, prevEnd, cancellationToken);
        summary.CompletedInPeriod = await CountCompletedAsync(connection, organizationId, periodStart, periodEnd, cancellationToken);
        summary.PreviousCompletedInPeriod = await CountCompletedAsync(connection, organizationId, prevStart, prevEnd, cancellationToken);
        summary.AverageCompletionDays = await AverageCompletionDaysAsync(connection, organizationId, periodStart, periodEnd, cancellationToken);
        summary.PreviousAverageCompletionDays = await AverageCompletionDaysAsync(connection, organizationId, prevStart, prevEnd, cancellationToken);

        summary.UpcomingStarts = await connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(1) FROM dbo.nh_hr_employee_onboardings
WHERE organization_id = @OrganizationId AND status = 'NotStarted' AND start_date BETWEEN @Today AND @Horizon;",
            new { OrganizationId = organizationId, Today = today, Horizon = today.AddDays(7) }, cancellationToken: cancellationToken));

        // Progress / overdue figures reuse the pure rules so they always agree with list and detail views.
        var states = (await connection.QueryAsync<OnboardingTaskStateRow>(new CommandDefinition(
            TaskStateSelect + @"
WHERE organization_id = @OrganizationId
  AND onboarding_id IN (SELECT id FROM dbo.nh_hr_employee_onboardings
                        WHERE organization_id = @OrganizationId AND status IN ('NotStarted', 'InProgress', 'InReview'));",
            org, cancellationToken: cancellationToken))).ToList();
        var grouped = states.GroupBy(s => s.OnboardingId).ToList();
        summary.OverdueTasks = grouped.Sum(g => OnboardingValidation.CountOverdue(g, today));
        summary.OverdueOnboardings = grouped.Count(g => OnboardingValidation.CountOverdue(g, today) > 0);
        var activeIds = grouped.Count;
        summary.AverageProgressPercent = activeIds == 0
            ? 0
            : Math.Round(grouped.Average(g => OnboardingValidation.ComputeProgressPercent(g)), 1);

        return summary;
    }

    private static Task<int> CountStartedAsync(IDbConnection connection, Guid organizationId, DateTime start, DateTime end, CancellationToken ct) =>
        connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(1) FROM dbo.nh_hr_employee_onboardings
WHERE organization_id = @OrganizationId AND status <> 'Cancelled' AND start_date BETWEEN @Start AND @End;",
            new { OrganizationId = organizationId, Start = start, End = end }, cancellationToken: ct));

    private static Task<int> CountCompletedAsync(IDbConnection connection, Guid organizationId, DateTime start, DateTime end, CancellationToken ct) =>
        connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(1) FROM dbo.nh_hr_employee_onboardings
WHERE organization_id = @OrganizationId AND status = 'Completed' AND actual_completion_date BETWEEN @Start AND @End;",
            new { OrganizationId = organizationId, Start = start, End = end }, cancellationToken: ct));

    private static async Task<double> AverageCompletionDaysAsync(IDbConnection connection, Guid organizationId, DateTime start, DateTime end, CancellationToken ct)
    {
        var avg = await connection.ExecuteScalarAsync<double?>(new CommandDefinition(@"
SELECT AVG(CAST(DATEDIFF(day, start_date, actual_completion_date) AS float))
FROM dbo.nh_hr_employee_onboardings
WHERE organization_id = @OrganizationId AND status = 'Completed'
  AND start_date IS NOT NULL AND actual_completion_date BETWEEN @Start AND @End;",
            new { OrganizationId = organizationId, Start = start, End = end }, cancellationToken: ct));
        return avg.HasValue ? Math.Round(avg.Value, 1) : 0;
    }

    // -------------------------------------------------------------------------
    // Notifications
    // -------------------------------------------------------------------------

    private sealed class NotificationSettingRow
    {
        public string TriggerCode { get; set; } = string.Empty;
        public bool IsEnabled { get; set; }
        public bool ChannelEmail { get; set; }
        public bool ChannelWhatsapp { get; set; }
        public int? DaysBefore { get; set; }
        public string? RecipientsJson { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
    }

    private const string NotificationSettingSelect = @"
SELECT trigger_code AS TriggerCode, is_enabled AS IsEnabled, channel_email AS ChannelEmail,
       channel_whatsapp AS ChannelWhatsapp, days_before AS DaysBefore, recipients_json AS RecipientsJson,
       updated_at AS UpdatedAt
FROM dbo.nh_hr_onboarding_notification_settings";

    public async Task<IReadOnlyList<OnboardingNotificationSettingDto>> GetNotificationSettingsAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var rows = await connection.QueryAsync<NotificationSettingRow>(new CommandDefinition(
            NotificationSettingSelect + " WHERE organization_id = @OrganizationId;",
            new { OrganizationId = organizationId }, cancellationToken: cancellationToken));

        var order = OnboardingTriggers.All.ToList();
        return rows.Select(ToSettingDto)
            .OrderBy(s => { var i = order.IndexOf(s.TriggerCode); return i < 0 ? int.MaxValue : i; })
            .ToList();
    }

    public async Task<OnboardingNotificationSettingDto?> GetNotificationSettingAsync(Guid organizationId, string triggerCode, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var row = await connection.QuerySingleOrDefaultAsync<NotificationSettingRow>(new CommandDefinition(
            NotificationSettingSelect + " WHERE organization_id = @OrganizationId AND trigger_code = @TriggerCode;",
            new { OrganizationId = organizationId, TriggerCode = triggerCode }, cancellationToken: cancellationToken));
        return row == null ? null : ToSettingDto(row);
    }

    public async Task UpsertNotificationSettingAsync(Guid organizationId, UpdateOnboardingNotificationSettingRequest setting, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        var recipients = setting.Recipients == null ? null : JsonSerializer.Serialize(setting.Recipients);
        var args = new
        {
            OrganizationId = organizationId, setting.TriggerCode, setting.IsEnabled, setting.ChannelEmail, setting.ChannelWhatsapp,
            setting.DaysBefore, RecipientsJson = recipients, Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId, Id = Guid.NewGuid()
        };

        using var connection = OpenConnection();
        var updated = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_onboarding_notification_settings
SET is_enabled = @IsEnabled, channel_email = @ChannelEmail, channel_whatsapp = @ChannelWhatsapp,
    days_before = @DaysBefore, recipients_json = ISNULL(@RecipientsJson, recipients_json),
    updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND trigger_code = @TriggerCode;", args, cancellationToken: cancellationToken));
        if (updated > 0) return;

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_onboarding_notification_settings
    (id, organization_id, trigger_code, is_enabled, channel_email, channel_whatsapp, days_before, recipients_json, updated_at, updated_by)
VALUES
    (@Id, @OrganizationId, @TriggerCode, @IsEnabled, @ChannelEmail, @ChannelWhatsapp, @DaysBefore, @RecipientsJson, @Now, @ActorUserId);",
                args, cancellationToken: cancellationToken));
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // A concurrent request created the row first; apply this update on top of it.
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_onboarding_notification_settings
SET is_enabled = @IsEnabled, channel_email = @ChannelEmail, channel_whatsapp = @ChannelWhatsapp,
    days_before = @DaysBefore, recipients_json = ISNULL(@RecipientsJson, recipients_json),
    updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND trigger_code = @TriggerCode;", args, cancellationToken: cancellationToken));
        }
    }

    private static OnboardingNotificationSettingDto ToSettingDto(NotificationSettingRow row)
    {
        var recipients = new List<string>();
        if (!string.IsNullOrWhiteSpace(row.RecipientsJson))
        {
            try
            {
                recipients = JsonSerializer.Deserialize<List<string>>(row.RecipientsJson) ?? new List<string>();
            }
            catch (JsonException)
            {
                recipients = new List<string>();
            }
        }

        return new OnboardingNotificationSettingDto
        {
            TriggerCode = row.TriggerCode,
            IsEnabled = row.IsEnabled,
            ChannelEmail = row.ChannelEmail,
            ChannelWhatsapp = row.ChannelWhatsapp,
            DaysBefore = row.DaysBefore,
            Recipients = recipients,
            UpdatedAt = row.UpdatedAt
        };
    }

    public async Task<IReadOnlyList<OnboardingNotificationDto>> ListNotificationsAsync(Guid organizationId, Guid? onboardingId, int take, CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT TOP (@Take) id AS Id, onboarding_id AS OnboardingId, trigger_code AS TriggerCode, channel AS Channel,
       recipient AS Recipient, status AS Status, error_message AS ErrorMessage, created_at AS CreatedAt
FROM dbo.nh_hr_onboarding_notifications
WHERE organization_id = @OrganizationId AND (@OnboardingId IS NULL OR onboarding_id = @OnboardingId)
ORDER BY created_at DESC;";

        using var connection = OpenConnection();
        return (await connection.QueryAsync<OnboardingNotificationDto>(new CommandDefinition(sql,
            new { OrganizationId = organizationId, OnboardingId = onboardingId, Take = Math.Clamp(take, 1, 500) },
            cancellationToken: cancellationToken))).ToList();
    }

    public async Task<bool> RecordNotificationAttemptAsync(
        Guid organizationId, Guid? onboardingId, string triggerCode, string dedupeKey, string channel,
        string? recipient, string status, string? errorMessage, CancellationToken cancellationToken = default)
    {
        var key = dedupeKey.Length > 200 ? dedupeKey[..200] : dedupeKey;
        using var connection = OpenConnection();
        try
        {
            var inserted = await connection.ExecuteAsync(new CommandDefinition(@"
IF NOT EXISTS (SELECT 1 FROM dbo.nh_hr_onboarding_notifications WHERE organization_id = @OrganizationId AND dedupe_key = @DedupeKey)
BEGIN
    INSERT INTO dbo.nh_hr_onboarding_notifications
        (id, organization_id, onboarding_id, trigger_code, dedupe_key, channel, recipient, status, error_message, created_at)
    VALUES
        (@Id, @OrganizationId, @OnboardingId, @TriggerCode, @DedupeKey, @Channel, @Recipient, @Status, @ErrorMessage, @Now);
END;",
                new
                {
                    Id = Guid.NewGuid(), OrganizationId = organizationId, OnboardingId = onboardingId, TriggerCode = triggerCode,
                    DedupeKey = key, Channel = channel, Recipient = Trim(recipient), Status = status,
                    ErrorMessage = errorMessage is { Length: > 1000 } ? errorMessage[..1000] : errorMessage, Now = DateTimeOffset.UtcNow
                }, cancellationToken: cancellationToken));
            return inserted > 0;
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // Lost a race with a concurrent identical attempt: treat as duplicate.
            return false;
        }
    }

    public async Task UpdateNotificationAttemptAsync(Guid organizationId, string dedupeKey, string status, string? errorMessage, CancellationToken cancellationToken = default)
    {
        var key = dedupeKey.Length > 200 ? dedupeKey[..200] : dedupeKey;
        using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_onboarding_notifications SET status = @Status, error_message = @ErrorMessage
WHERE organization_id = @OrganizationId AND dedupe_key = @DedupeKey;",
            new
            {
                OrganizationId = organizationId, DedupeKey = key, Status = status,
                ErrorMessage = errorMessage is { Length: > 1000 } ? errorMessage[..1000] : errorMessage
            }, cancellationToken: cancellationToken));
    }

    private sealed class RecipientRow
    {
        public string? Email { get; set; }
        public string? Name { get; set; }
    }

    public async Task<IReadOnlyList<OnboardingNotificationRecipient>> ResolveNotificationRecipientsAsync(
        Guid organizationId, Guid onboardingId, Guid? taskId, IReadOnlyCollection<string> roles, CancellationToken cancellationToken = default)
    {
        var result = new List<OnboardingNotificationRecipient>();
        using var connection = OpenConnection();
        var args = new { OrganizationId = organizationId, OnboardingId = onboardingId, TaskId = taskId };

        foreach (var role in roles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string? sql = role.ToLowerInvariant() switch
            {
                "owner" => @"
SELECT u.email AS Email, COALESCE(u.display_name, u.email) AS Name
FROM dbo.nh_hr_employee_onboardings o
INNER JOIN dbo.nh_users u ON u.id = o.owner_user_id AND u.org_id = o.organization_id
WHERE o.organization_id = @OrganizationId AND o.id = @OnboardingId;",
                "assignee" when taskId.HasValue => @"
SELECT u.email AS Email, COALESCE(u.display_name, u.email) AS Name
FROM dbo.nh_hr_onboarding_tasks t
INNER JOIN dbo.nh_users u ON u.id = t.assigned_user_id AND u.org_id = t.organization_id
WHERE t.organization_id = @OrganizationId AND t.id = @TaskId;",
                "employee" => @"
SELECT e.work_email AS Email, " + EmployeeNameSql + @" AS Name
FROM dbo.nh_hr_employee_onboardings o
INNER JOIN dbo.nh_hr_employees e ON e.id = o.employee_id AND e.organization_id = o.organization_id
WHERE o.organization_id = @OrganizationId AND o.id = @OnboardingId;",
                "directmanager" => @"
SELECT e.work_email AS Email, " + EmployeeNameSql + @" AS Name
FROM dbo.nh_hr_employee_onboardings o
INNER JOIN dbo.nh_hr_employees emp ON emp.id = o.employee_id AND emp.organization_id = o.organization_id
INNER JOIN dbo.nh_hr_employees e ON e.id = emp.manager_id AND e.organization_id = emp.organization_id
WHERE o.organization_id = @OrganizationId AND o.id = @OnboardingId;",
                _ => null
            };
            if (sql == null) continue;

            var row = await connection.QueryFirstOrDefaultAsync<RecipientRow>(new CommandDefinition(sql, args, cancellationToken: cancellationToken));
            if (row != null) result.Add(new OnboardingNotificationRecipient(role, Trim(row.Email), Trim(row.Name)));
        }
        return result;
    }

    // -------------------------------------------------------------------------
    // Defaults (templates + notification settings)
    // -------------------------------------------------------------------------

    private sealed record SeedTask(
        string Title, string Category, string Assignment, int? Offset,
        bool Required = true, string? DocCategory = null, bool Approval = false, string? Description = null);

    private static readonly SeedTask[] GeneralTasks =
    [
        new("Sign employment contract", OnboardingTaskCategories.Documentation, OnboardingAssignmentTypes.HR, -2, true, "Contract", true,
            "Collect the signed employment agreement before the first day."),
        new("Submit government-issued ID", OnboardingTaskCategories.Documentation, OnboardingAssignmentTypes.Employee, 0, true, "Identification", true),
        new("Submit tax and social security forms", OnboardingTaskCategories.Compliance, OnboardingAssignmentTypes.Employee, 3, true, "Tax", true),
        new("Provide bank details for payroll", OnboardingTaskCategories.Administrative, OnboardingAssignmentTypes.Employee, 3, true, "Banking"),
        new("Complete emergency contact information", OnboardingTaskCategories.Administrative, OnboardingAssignmentTypes.Employee, 1),
        new("Prepare workspace and equipment", OnboardingTaskCategories.ITEquipment, OnboardingAssignmentTypes.IT, -1),
        new("Create email and system accounts", OnboardingTaskCategories.ITEquipment, OnboardingAssignmentTypes.IT, 0),
        new("Welcome meeting with direct manager", OnboardingTaskCategories.Meetings, OnboardingAssignmentTypes.DirectManager, 0),
        new("Company orientation and culture session", OnboardingTaskCategories.Orientation, OnboardingAssignmentTypes.HR, 1),
        new("Review employee handbook and policies", OnboardingTaskCategories.Compliance, OnboardingAssignmentTypes.Employee, 5),
        new("Role-specific training plan", OnboardingTaskCategories.Training, OnboardingAssignmentTypes.DirectManager, 7),
        new("30-day check-in", OnboardingTaskCategories.Meetings, OnboardingAssignmentTypes.DirectManager, 30, false)
    ];

    private static readonly SeedTask[] ItTasks =
    [
        new("Sign employment contract", OnboardingTaskCategories.Documentation, OnboardingAssignmentTypes.HR, -2, true, "Contract", true),
        new("Sign NDA and IP assignment agreement", OnboardingTaskCategories.Compliance, OnboardingAssignmentTypes.HR, -2, true, "NDA", true),
        new("Submit government-issued ID", OnboardingTaskCategories.Documentation, OnboardingAssignmentTypes.Employee, 0, true, "Identification"),
        new("Provide bank details for payroll", OnboardingTaskCategories.Administrative, OnboardingAssignmentTypes.Employee, 3, true, "Banking"),
        new("Provision and image laptop", OnboardingTaskCategories.ITEquipment, OnboardingAssignmentTypes.IT, -2),
        new("Create email, SSO and MFA accounts", OnboardingTaskCategories.ITEquipment, OnboardingAssignmentTypes.IT, -1),
        new("Grant repository, CI/CD and cloud access", OnboardingTaskCategories.ITEquipment, OnboardingAssignmentTypes.IT, 1, true, null, true),
        new("Set up development environment", OnboardingTaskCategories.Training, OnboardingAssignmentTypes.Employee, 2),
        new("Security awareness training", OnboardingTaskCategories.Compliance, OnboardingAssignmentTypes.Employee, 3),
        new("Welcome meeting with direct manager", OnboardingTaskCategories.Meetings, OnboardingAssignmentTypes.DirectManager, 0),
        new("Architecture and codebase walkthrough", OnboardingTaskCategories.Training, OnboardingAssignmentTypes.DirectManager, 5),
        new("Ship a first small change", OnboardingTaskCategories.Training, OnboardingAssignmentTypes.DirectManager, 14, false),
        new("30-day check-in", OnboardingTaskCategories.Meetings, OnboardingAssignmentTypes.DirectManager, 30, false)
    ];

    private static readonly SeedTask[] HospitalityTasks =
    [
        new("Sign employment contract", OnboardingTaskCategories.Documentation, OnboardingAssignmentTypes.HR, -2, true, "Contract", true),
        new("Submit government-issued ID", OnboardingTaskCategories.Documentation, OnboardingAssignmentTypes.Employee, 0, true, "Identification"),
        new("Submit health card / medical certificate", OnboardingTaskCategories.Compliance, OnboardingAssignmentTypes.Employee, 3, true, "Health", true),
        new("Food handling and safety certification", OnboardingTaskCategories.Compliance, OnboardingAssignmentTypes.Employee, 7, true, "Certification", true),
        new("Uniform fitting and issue", OnboardingTaskCategories.Administrative, OnboardingAssignmentTypes.HR, 0),
        new("Property tour and emergency procedures", OnboardingTaskCategories.Orientation, OnboardingAssignmentTypes.DirectManager, 0),
        new("Guest service standards training", OnboardingTaskCategories.Training, OnboardingAssignmentTypes.DirectManager, 2),
        new("Property management / POS system training", OnboardingTaskCategories.Training, OnboardingAssignmentTypes.DirectManager, 4),
        new("Create staff system accounts and key access", OnboardingTaskCategories.ITEquipment, OnboardingAssignmentTypes.IT, 0),
        new("Shadow shifts with a senior team member", OnboardingTaskCategories.Training, OnboardingAssignmentTypes.DirectManager, 10),
        new("Review employee handbook and policies", OnboardingTaskCategories.Compliance, OnboardingAssignmentTypes.Employee, 5),
        new("30-day check-in", OnboardingTaskCategories.Meetings, OnboardingAssignmentTypes.DirectManager, 30, false)
    ];

    private static readonly (string Trigger, bool Enabled, bool Email, bool WhatsApp, int? DaysBefore, string[] Recipients)[] DefaultNotificationSettings =
    [
        (OnboardingTriggers.OnboardingCreated, true, true, false, null, [OnboardingRecipientRoles.Owner]),
        (OnboardingTriggers.OnboardingStarted, true, true, false, null, [OnboardingRecipientRoles.Owner, OnboardingRecipientRoles.DirectManager]),
        (OnboardingTriggers.OnboardingInReview, true, true, false, null, [OnboardingRecipientRoles.Owner]),
        (OnboardingTriggers.OnboardingCompleted, true, true, false, null, [OnboardingRecipientRoles.Owner, OnboardingRecipientRoles.DirectManager]),
        (OnboardingTriggers.OnboardingCancelled, true, true, false, null, [OnboardingRecipientRoles.Owner]),
        (OnboardingTriggers.TaskAssigned, true, true, false, null, [OnboardingRecipientRoles.Assignee]),
        (OnboardingTriggers.TaskDueSoon, true, true, false, 2, [OnboardingRecipientRoles.Assignee]),
        (OnboardingTriggers.TaskOverdue, true, true, false, null, [OnboardingRecipientRoles.Assignee, OnboardingRecipientRoles.Owner]),
        (OnboardingTriggers.TaskApprovalRequested, true, true, false, null, [OnboardingRecipientRoles.Owner]),
        (OnboardingTriggers.TaskApproved, false, true, false, null, [OnboardingRecipientRoles.Assignee]),
        (OnboardingTriggers.TaskRejected, true, true, false, null, [OnboardingRecipientRoles.Assignee])
    ];

    private async Task EnsureOrgDefaultsCoreAsync(IDbConnection connection, IDbTransaction tx, Guid organizationId, CancellationToken cancellationToken)
    {
        // Serialize concurrent first requests for the same org so defaults are never seeded twice.
        await connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
DECLARE @r int;
EXEC @r = sp_getapplock @Resource = @Resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000;
SELECT @r;",
            new { Resource = $"nh-onboarding-defaults:{organizationId:N}" }, tx, cancellationToken: cancellationToken));

        var templateCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM dbo.nh_hr_onboarding_templates WHERE organization_id = @OrganizationId;",
            new { OrganizationId = organizationId }, tx, cancellationToken: cancellationToken));

        if (templateCount == 0)
        {
            async Task<Guid?> DepartmentId(string name) => await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT TOP 1 id FROM dbo.nh_hr_departments WHERE organization_id = @OrganizationId AND name = @Name;",
                new { OrganizationId = organizationId, Name = name }, tx, cancellationToken: cancellationToken));

            await SeedTemplateAsync(connection, tx, organizationId, "General",
                "Standard onboarding for any new hire.", null, 30, isDefault: true, GeneralTasks, cancellationToken);
            await SeedTemplateAsync(connection, tx, organizationId, "IT",
                "Onboarding for engineering and IT roles: equipment, access and security.", await DepartmentId("IT"), 45, false, ItTasks, cancellationToken);
            await SeedTemplateAsync(connection, tx, organizationId, "Hospitality",
                "Onboarding for front-of-house and guest-facing roles: certifications, uniform and service standards.",
                await DepartmentId("Hospitality"), 30, false, HospitalityTasks, cancellationToken);
        }

        foreach (var s in DefaultNotificationSettings)
        {
            await connection.ExecuteAsync(new CommandDefinition(@"
IF NOT EXISTS (SELECT 1 FROM dbo.nh_hr_onboarding_notification_settings WHERE organization_id = @OrganizationId AND trigger_code = @TriggerCode)
BEGIN
    INSERT INTO dbo.nh_hr_onboarding_notification_settings
        (id, organization_id, trigger_code, is_enabled, channel_email, channel_whatsapp, days_before, recipients_json)
    VALUES
        (@Id, @OrganizationId, @TriggerCode, @IsEnabled, @ChannelEmail, @ChannelWhatsapp, @DaysBefore, @RecipientsJson);
END;",
                new
                {
                    Id = Guid.NewGuid(), OrganizationId = organizationId, TriggerCode = s.Trigger, IsEnabled = s.Enabled,
                    ChannelEmail = s.Email, ChannelWhatsapp = s.WhatsApp, DaysBefore = s.DaysBefore,
                    RecipientsJson = JsonSerializer.Serialize(s.Recipients)
                }, tx, cancellationToken: cancellationToken));
        }
    }

    private static Task SeedTemplateAsync(
        IDbConnection connection, IDbTransaction tx, Guid organizationId, string name, string description,
        Guid? departmentId, int durationDays, bool isDefault, SeedTask[] tasks, CancellationToken cancellationToken)
    {
        var request = new UpsertOnboardingTemplateRequest
        {
            Name = name,
            Description = description,
            DepartmentId = departmentId,
            EstimatedDurationDays = durationDays,
            IsActive = true,
            IsDefault = isDefault,
            Tasks = tasks.Select(t => new UpsertOnboardingTemplateTaskRequest
            {
                Title = t.Title,
                Description = t.Description,
                Category = t.Category,
                AssignmentType = t.Assignment,
                DueOffsetDays = t.Offset,
                IsRequired = t.Required,
                RequiresDocument = t.DocCategory != null,
                DocumentCategory = t.DocCategory,
                RequiresApproval = t.Approval
            }).ToList()
        };
        return InsertTemplateCoreAsync(connection, tx, organizationId, request, null, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------

    /// <summary>Loads task states for the given onboardings and fills progress / overdue figures.</summary>
    private static async Task AttachProgressAsync(
        IDbConnection connection, Guid organizationId, IReadOnlyList<OnboardingListItemDto> items, DateTime today, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return;

        var ids = items.Select(i => i.Id).ToArray();
        var states = (await connection.QueryAsync<OnboardingTaskStateRow>(new CommandDefinition(
            TaskStateSelect + " WHERE organization_id = @OrganizationId AND onboarding_id IN @Ids;",
            new { OrganizationId = organizationId, Ids = ids }, cancellationToken: cancellationToken)))
            .GroupBy(s => s.OnboardingId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var item in items)
        {
            states.TryGetValue(item.Id, out var tasks);
            ApplyProgress(item, (IReadOnlyList<IOnboardingTaskState>?)tasks ?? Array.Empty<IOnboardingTaskState>(), today);
        }
    }

    private static void ApplyProgress(OnboardingListItemDto item, IEnumerable<IOnboardingTaskState> tasks, DateTime? today = null)
    {
        var list = tasks.ToList();
        var (completed, total) = OnboardingValidation.CountProgress(list);
        item.CompletedTasks = completed;
        item.TotalTasks = total;
        item.ProgressPercent = OnboardingValidation.ComputeProgressPercent(list);
        item.OverdueCount = OnboardingValidation.IsActiveStatus(item.Status)
            ? OnboardingValidation.CountOverdue(list, today ?? DateTime.UtcNow.Date)
            : 0;
    }

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
            $"Core HR onboarding schema script not found. Expected sql/{SchemaScriptFileName}.");
    }

    private static string BuildOrderBy(string? sort, string? dir)
    {
        var direction = string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
        var template = !string.IsNullOrWhiteSpace(sort) && SortColumns.TryGetValue(sort.Trim(), out var t) ? t : SortColumns["startDate"];
        return template.Replace("{dir}", direction);
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
