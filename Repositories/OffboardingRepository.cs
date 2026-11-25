using System.Data;
using System.Data.SqlClient;
using System.Text.Json;
using Dapper;
using nexthire_api.Data;
using nexthire_api.DTOs;
using nexthire_api.DTOs.CoreHr;
using nexthire_api.Services;

namespace nexthire_api.Repositories;

/// <summary>Defaults applied to every new offboarding (access systems, settlement checklist, exit questionnaire).</summary>
public static class OffboardingDefaults
{
    /// <summary>Systems whose access must be revoked / verified before an offboarding can complete.</summary>
    public static readonly IReadOnlyList<string> AccessSystems =
    [
        "Company Email",
        "Microsoft 365",
        "VPN",
        "GitHub",
        "Internal Applications",
        "Employee Portal"
    ];

    public sealed record SettlementDefault(string Code, string Title, bool Required);

    public static readonly IReadOnlyList<SettlementDefault> Settlement =
    [
        new("FinalPay", "Final salary calculation and payment", true),
        new("UnusedLeave", "Accrued / unused leave payout", true),
        new("ExpenseReimbursement", "Pending expense reimbursements", true),
        new("BenefitsTermination", "Benefits termination and provider notification", true),
        new("TaxDocuments", "Tax and final pay documents", false),
        new("Severance", "Severance / separation payment", false)
    ];

    public const string ExitQuestionnaireJson =
        "[{\"id\":\"reason\",\"text\":\"What is the main reason for leaving?\"}," +
        "{\"id\":\"experience\",\"text\":\"How would you describe your overall experience at the company?\"}," +
        "{\"id\":\"manager\",\"text\":\"How was your relationship with your manager and team?\"}," +
        "{\"id\":\"growth\",\"text\":\"Did you have the growth and development opportunities you wanted?\"}," +
        "{\"id\":\"improve\",\"text\":\"What could we do better?\"}," +
        "{\"id\":\"recommend\",\"text\":\"Would you recommend the company as a place to work?\"}]";
}

/// <summary>
/// Dapper/SQL Server implementation of Core HR offboarding persistence.
/// All queries are parameterized and scoped by organization_id.
/// </summary>
public class OffboardingRepository : IOffboardingRepository
{
    private const string SchemaScriptFileName = "011-core-hr-offboarding-schema.sql";

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<OffboardingRepository> _logger;

    public OffboardingRepository(IDbConnectionFactory connectionFactory, ILogger<OffboardingRepository> logger)
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

    /// <summary>List / pipeline columns. Never includes confidential fields.</summary>
    private const string OffboardingListSelect = @"
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
    o.owner_user_id AS OwnerUserId,
    COALESCE(u.display_name, u.email) AS OwnerName,
    o.separation_type AS SeparationType,
    o.notice_date AS NoticeDate,
    o.process_start_date AS ProcessStartDate,
    o.last_working_day AS LastWorkingDay,
    o.termination_effective_date AS TerminationEffectiveDate,
    o.expected_completion_date AS ExpectedCompletionDate,
    o.actual_completion_date AS ActualCompletionDate,
    o.status AS Status,
    o.employment_status_applied AS EmploymentStatusApplied,
    (SELECT COUNT(1) FROM dbo.nh_hr_offboarding_assets a
      WHERE a.organization_id = o.organization_id AND a.offboarding_id = o.id
        AND a.return_required = 1 AND a.status NOT IN ('Returned', 'Waived')) AS PendingAssets,
    (SELECT COUNT(1) FROM dbo.nh_hr_offboarding_access_items x
      WHERE x.organization_id = o.organization_id AND x.offboarding_id = o.id
        AND x.status NOT IN ('Revoked', 'Verified', 'NotApplicable')) AS PendingAccessItems,
    o.created_at AS CreatedAt";

    /// <summary>Detail adds confidential columns; the service redacts them per capability.</summary>
    private const string OffboardingDetailSelect = OffboardingListSelect + @",
    o.template_version AS TemplateVersion,
    o.rehire_eligibility AS RehireEligibility,
    o.notify_employee AS NotifyEmployee,
    o.separation_reason AS SeparationReason,
    o.confidential_notes AS ConfidentialNotes,
    o.cancel_reason AS CancelReason,
    o.updated_at AS UpdatedAt";

    private const string OffboardingJoins = @"
FROM dbo.nh_hr_employee_offboardings o
INNER JOIN dbo.nh_hr_employees e ON e.id = o.employee_id AND e.organization_id = o.organization_id
LEFT JOIN dbo.nh_hr_departments d ON d.id = e.department_id AND d.organization_id = e.organization_id
LEFT JOIN dbo.nh_hr_job_titles jt ON jt.id = e.job_title_id AND jt.organization_id = e.organization_id
LEFT JOIN dbo.nh_hr_offboarding_templates t ON t.id = o.template_id AND t.organization_id = o.organization_id
LEFT JOIN dbo.nh_users u ON u.id = o.owner_user_id AND u.org_id = o.organization_id";

    // The separation reason / confidential notes are intentionally NOT searchable.
    private const string OffboardingFilters = @"
WHERE o.organization_id = @OrganizationId
  AND (@Status IS NULL OR o.status = @Status)
  AND (@DepartmentId IS NULL OR e.department_id = @DepartmentId)
  AND (@OwnerUserId IS NULL OR o.owner_user_id = @OwnerUserId)
  AND (@TemplateId IS NULL OR o.template_id = @TemplateId)
  AND (@SeparationType IS NULL OR o.separation_type = @SeparationType)
  AND (@LastWorkingFrom IS NULL OR o.last_working_day >= @LastWorkingFrom)
  AND (@LastWorkingTo IS NULL OR o.last_working_day <= @LastWorkingTo)
  AND (@OverdueOnly = 0 OR (
        o.status IN ('Scheduled', 'InProgress', 'PendingClearance')
        AND EXISTS (
            SELECT 1 FROM dbo.nh_hr_offboarding_tasks ot
            WHERE ot.organization_id = o.organization_id AND ot.offboarding_id = o.id
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
        ["lastWorkingDay"] = "o.last_working_day {dir}",
        ["expectedCompletion"] = "o.expected_completion_date {dir}",
        ["status"] = "o.status {dir}",
        ["created"] = "o.created_at {dir}"
    };

    private const string TaskSelect = @"
SELECT
    t.id AS Id, t.offboarding_id AS OffboardingId, t.template_task_id AS TemplateTaskId,
    t.title AS Title, t.description AS Description, t.category AS Category,
    t.assignment_type AS AssignmentType, t.assigned_user_id AS AssignedUserId,
    COALESCE(u.display_name, u.email) AS AssignedUserName,
    t.due_date AS DueDate, t.status AS Status, t.priority AS Priority, t.is_required AS IsRequired,
    t.requires_document AS RequiresDocument, t.document_category AS DocumentCategory,
    t.requires_approval AS RequiresApproval, t.approval_status AS ApprovalStatus,
    t.approved_by AS ApprovedBy, t.approved_at AS ApprovedAt,
    t.completed_at AS CompletedAt, t.completed_by AS CompletedBy, t.completion_notes AS CompletionNotes,
    t.evidence_url AS EvidenceUrl,
    t.display_order AS DisplayOrder, t.depends_on_task_id AS DependsOnTaskId, t.ats_task_id AS AtsTaskId,
    t.is_waived AS IsWaived, t.waive_reason AS WaiveReason, t.created_at AS CreatedAt
FROM dbo.nh_hr_offboarding_tasks t
LEFT JOIN dbo.nh_users u ON u.id = t.assigned_user_id AND u.org_id = t.organization_id";

    private const string TaskStateSelect = @"
SELECT id AS Id, offboarding_id AS OffboardingId, title AS Title, status AS Status, is_waived AS IsWaived,
       is_required AS IsRequired, requires_approval AS RequiresApproval, approval_status AS ApprovalStatus,
       due_date AS DueDate, depends_on_task_id AS DependsOnTaskId
FROM dbo.nh_hr_offboarding_tasks";

    private const string DocumentSelect = @"
SELECT id AS Id, offboarding_id AS OffboardingId, offboarding_task_id AS OffboardingTaskId,
       employee_document_id AS EmployeeDocumentId, category AS Category, name AS Name,
       is_required AS IsRequired, status AS Status, created_at AS CreatedAt
FROM dbo.nh_hr_offboarding_documents";

    private const string AssetSelect = @"
SELECT id AS Id, offboarding_id AS OffboardingId, asset_name AS AssetName, asset_type AS AssetType,
       serial_number AS SerialNumber, return_required AS ReturnRequired, expected_return_date AS ExpectedReturnDate,
       actual_return_date AS ActualReturnDate, condition_notes AS ConditionNotes, received_by AS ReceivedBy,
       status AS Status, notes AS Notes, evidence_url AS EvidenceUrl, waive_reason AS WaiveReason,
       created_at AS CreatedAt, updated_at AS UpdatedAt
FROM dbo.nh_hr_offboarding_assets";

    private const string AccessSelect = @"
SELECT x.id AS Id, x.offboarding_id AS OffboardingId, x.system_name AS SystemName,
       x.responsible_user_id AS ResponsibleUserId, COALESCE(u.display_name, u.email) AS ResponsibleUserName,
       x.scheduled_at AS ScheduledAt, x.revoked_at AS RevokedAt, x.status AS Status, x.evidence_url AS EvidenceUrl,
       x.verified_by AS VerifiedBy, x.verified_at AS VerifiedAt, x.notes AS Notes,
       x.created_at AS CreatedAt, x.updated_at AS UpdatedAt
FROM dbo.nh_hr_offboarding_access_items x
LEFT JOIN dbo.nh_users u ON u.id = x.responsible_user_id AND u.org_id = x.organization_id";

    private const string SettlementSelect = @"
SELECT id AS Id, offboarding_id AS OffboardingId, item_code AS ItemCode, title AS Title, is_required AS IsRequired,
       status AS Status, amount AS Amount, currency AS Currency, notes AS Notes, evidence_url AS EvidenceUrl,
       completed_at AS CompletedAt, completed_by AS CompletedBy, created_at AS CreatedAt
FROM dbo.nh_hr_offboarding_settlement_items";

    private const string ExitInterviewSelect = @"
SELECT x.id AS Id, x.offboarding_id AS OffboardingId, x.interviewer_user_id AS InterviewerUserId,
       COALESCE(u.display_name, u.email) AS InterviewerName, x.scheduled_at AS ScheduledAt, x.status AS Status,
       x.questionnaire_json AS QuestionnaireJson, x.responses_json AS ResponsesJson, x.hr_notes AS HrNotes,
       x.completed_at AS CompletedAt, x.created_at AS CreatedAt, x.updated_at AS UpdatedAt
FROM dbo.nh_hr_offboarding_exit_interviews x
LEFT JOIN dbo.nh_users u ON u.id = x.interviewer_user_id AND u.org_id = x.organization_id";

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

    private sealed record DemoScenario(
        string Status, string SeparationType, int LastWorkingDayOffset, double CompletionRatio,
        bool UseTerminatedEmployee = false, int? CompletedDaysAgo = null, string? CancelReason = null);

    // Termination effective dates stay in the future (or the employee is already Terminated) so that seeding
    // never flips a demo employee's employment status by accident.
    private static readonly DemoScenario[] DemoScenarios =
    [
        new(OffboardingStatuses.Scheduled, SeparationTypes.VoluntaryResignation, 21, 0),
        new(OffboardingStatuses.InProgress, SeparationTypes.VoluntaryResignation, 10, 0.4),
        new(OffboardingStatuses.InProgress, SeparationTypes.ContractExpiration, 4, 0.25),   // behind schedule: overdue tasks
        new(OffboardingStatuses.PendingClearance, SeparationTypes.Retirement, 2, 1.0),
        new(OffboardingStatuses.Completed, SeparationTypes.VoluntaryResignation, -30, 1.0, UseTerminatedEmployee: true, CompletedDaysAgo: 20),
        new(OffboardingStatuses.Cancelled, SeparationTypes.VoluntaryResignation, 15, 0.1, CancelReason: "Employee accepted a counter-offer and stayed (demo data)")
    ];

    public async Task SeedDemoOffboardingsIfEmptyAsync(
        Guid organizationId,
        Guid? actorUserId,
        bool isDevelopment,
        CancellationToken cancellationToken = default)
    {
        if (!isDevelopment) return;

        using var connection = OpenConnection();

        var existing = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM dbo.nh_hr_employee_offboardings WHERE organization_id = @OrganizationId;",
            new { OrganizationId = organizationId }, cancellationToken: cancellationToken));
        if (existing > 0) return;

        var activeEmployeeIds = (await connection.QueryAsync<Guid>(new CommandDefinition(@"
SELECT TOP (@Take) e.id
FROM dbo.nh_hr_employees e
WHERE e.organization_id = @OrganizationId
  AND e.archived_at IS NULL
  AND e.employment_status IN ('Active', 'OnLeave')
ORDER BY e.hire_date, e.id;",
            new { OrganizationId = organizationId, Take = DemoScenarios.Length }, cancellationToken: cancellationToken))).ToList();

        var terminatedEmployeeIds = (await connection.QueryAsync<Guid>(new CommandDefinition(@"
SELECT TOP (@Take) e.id
FROM dbo.nh_hr_employees e
WHERE e.organization_id = @OrganizationId
  AND e.archived_at IS NULL
  AND e.employment_status = 'Terminated'
ORDER BY e.termination_date DESC, e.id;",
            new { OrganizationId = organizationId, Take = 1 }, cancellationToken: cancellationToken))).ToList();

        if (activeEmployeeIds.Count == 0 && terminatedEmployeeIds.Count == 0) return;

        Guid? ownerUserId = null;
        try
        {
            ownerUserId = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT TOP 1 id FROM dbo.nh_users WHERE org_id = @OrganizationId ORDER BY created_at;",
                new { OrganizationId = organizationId }, cancellationToken: cancellationToken));
        }
        catch (SqlException ex)
        {
            _logger.LogDebug(ex, "Could not resolve a demo offboarding owner for organization {OrganizationId}.", organizationId);
        }

        using var tx = connection.BeginTransaction();
        try
        {
            await EnsureOrgDefaultsCoreAsync(connection, tx, organizationId, cancellationToken);

            // Re-check under the transaction so concurrent first requests do not double seed.
            var again = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(1) FROM dbo.nh_hr_employee_offboardings WITH (UPDLOCK, HOLDLOCK) WHERE organization_id = @OrganizationId;",
                new { OrganizationId = organizationId }, tx, cancellationToken: cancellationToken));
            if (again > 0)
            {
                tx.Rollback();
                return;
            }

            var today = DateTime.UtcNow.Date;
            var now = DateTimeOffset.UtcNow;
            var activeQueue = new Queue<Guid>(activeEmployeeIds);
            var terminatedQueue = new Queue<Guid>(terminatedEmployeeIds);
            var seeded = 0;

            foreach (var scenario in DemoScenarios)
            {
                var pool = scenario.UseTerminatedEmployee ? terminatedQueue : activeQueue;
                if (pool.Count == 0) continue;
                var employeeId = pool.Dequeue();

                var templateId = await ResolveTemplateIdCoreAsync(connection, tx, organizationId, employeeId, scenario.SeparationType, cancellationToken);
                var lastWorkingDay = today.AddDays(scenario.LastWorkingDayOffset);

                var created = await InsertOffboardingCoreAsync(connection, tx, organizationId, new CreateOffboardingCommand
                {
                    EmployeeId = employeeId,
                    TemplateId = templateId,
                    OwnerUserId = ownerUserId,
                    SeparationType = scenario.SeparationType,
                    LastWorkingDay = lastWorkingDay,
                    NoticeDate = lastWorkingDay.AddDays(-14),
                    TerminationEffectiveDate = lastWorkingDay,
                    RehireEligibility = RehireEligibilities.Eligible,
                    InitialStatus = scenario.Status == OffboardingStatuses.Scheduled ? OffboardingStatuses.Scheduled : OffboardingStatuses.InProgress,
                    ActorUserId = actorUserId,
                    ActorName = "System"
                }, cancellationToken);

                // Complete a share of the tasks (in display order) to produce realistic progress.
                var taskIds = (await connection.QueryAsync<Guid>(new CommandDefinition(
                    "SELECT id FROM dbo.nh_hr_offboarding_tasks WHERE organization_id = @OrganizationId AND offboarding_id = @Id ORDER BY display_order, created_at;",
                    new { OrganizationId = organizationId, Id = created.Id }, tx, cancellationToken: cancellationToken))).ToList();
                var toComplete = (int)Math.Round(taskIds.Count * scenario.CompletionRatio, MidpointRounding.AwayFromZero);
                var fullyApproved = scenario.CompletionRatio >= 1.0;
                foreach (var taskId in taskIds.Take(toComplete))
                {
                    await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_offboarding_tasks
SET status = 'Completed', completed_at = @Now, completed_by = @ActorUserId,
    approval_status = CASE WHEN requires_approval = 1 THEN @ApprovalStatus ELSE NULL END,
    approved_by = CASE WHEN requires_approval = 1 AND @ApprovalStatus = 'Approved' THEN @ActorUserId ELSE NULL END,
    approved_at = CASE WHEN requires_approval = 1 AND @ApprovalStatus = 'Approved' THEN @Now ELSE NULL END,
    updated_at = @Now
WHERE organization_id = @OrganizationId AND id = @Id;",
                        new
                        {
                            OrganizationId = organizationId, Id = taskId, Now = now, ActorUserId = actorUserId,
                            ApprovalStatus = fullyApproved ? OffboardingApprovalStatuses.Approved : OffboardingApprovalStatuses.Pending
                        }, tx, cancellationToken: cancellationToken));
                }

                if (scenario.Status == OffboardingStatuses.Completed)
                    await ClearGatesForDemoAsync(connection, tx, organizationId, created.Id, now, actorUserId, cancellationToken);

                if (scenario.Status is OffboardingStatuses.PendingClearance or OffboardingStatuses.Completed or OffboardingStatuses.Cancelled)
                {
                    DateTime? completion = scenario.CompletedDaysAgo.HasValue ? today.AddDays(-scenario.CompletedDaysAgo.Value) : null;
                    await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_employee_offboardings
SET status = @Status, actual_completion_date = @CompletionDate, cancel_reason = @CancelReason, updated_at = @Now,
    process_start_date = CASE WHEN @Status = 'Completed' THEN DATEADD(day, -14, last_working_day) ELSE process_start_date END,
    -- the demo employee of a completed offboarding is already Terminated in the seeded employee data
    employment_status_applied = CASE WHEN @Status = 'Completed' THEN 1 ELSE employment_status_applied END
WHERE organization_id = @OrganizationId AND id = @Id;",
                        new
                        {
                            OrganizationId = organizationId, Id = created.Id, Status = scenario.Status,
                            CompletionDate = completion, scenario.CancelReason, Now = now
                        }, tx, cancellationToken: cancellationToken));
                    await InsertActivityCoreAsync(connection, tx, organizationId, created.Id, "StatusChanged",
                        $"Status set to {scenario.Status} (demo data)", null, actorUserId, "System", cancellationToken);
                }
                seeded++;
            }

            tx.Commit();
            _logger.LogInformation("Seeded {Count} demo offboardings for organization {OrganizationId}.", seeded, organizationId);
        }
        catch
        {
            try { tx.Rollback(); } catch (InvalidOperationException) { /* already completed */ }
            throw;
        }
    }

    private static async Task ClearGatesForDemoAsync(
        IDbConnection connection, IDbTransaction tx, Guid organizationId, Guid offboardingId,
        DateTimeOffset now, Guid? actorUserId, CancellationToken cancellationToken)
    {
        var args = new { OrganizationId = organizationId, Id = offboardingId, Now = now, ActorUserId = actorUserId, Today = now.UtcDateTime.Date };
        await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_offboarding_assets
SET status = 'Returned', actual_return_date = @Today, received_by = @ActorUserId, updated_at = @Now
WHERE organization_id = @OrganizationId AND offboarding_id = @Id AND return_required = 1;

UPDATE dbo.nh_hr_offboarding_access_items
SET status = 'Verified', revoked_at = @Now, verified_at = @Now, verified_by = @ActorUserId, updated_at = @Now
WHERE organization_id = @OrganizationId AND offboarding_id = @Id;

UPDATE dbo.nh_hr_offboarding_settlement_items
SET status = 'Completed', completed_at = @Now, completed_by = @ActorUserId
WHERE organization_id = @OrganizationId AND offboarding_id = @Id AND is_required = 1;

UPDATE dbo.nh_hr_offboarding_exit_interviews
SET status = 'Skipped', updated_at = @Now
WHERE organization_id = @OrganizationId AND offboarding_id = @Id;",
            args, tx, cancellationToken: cancellationToken));
    }

    // -------------------------------------------------------------------------
    // Templates
    // -------------------------------------------------------------------------

    private const string TemplateSelect = @"
SELECT t.id AS Id, t.name AS Name, t.description AS Description, t.separation_type AS SeparationType,
       t.department_id AS DepartmentId, d.name AS Department,
       t.job_title_id AS JobTitleId, jt.name AS JobTitle,
       t.employment_type_id AS EmploymentTypeId, et.name AS EmploymentType,
       t.estimated_duration_days AS EstimatedDurationDays,
       t.is_active AS IsActive, t.is_default AS IsDefault, t.version AS Version,
       (SELECT COUNT(1) FROM dbo.nh_hr_offboarding_template_tasks tt
         WHERE tt.template_id = t.id AND tt.organization_id = t.organization_id) AS TaskCount,
       CAST(CASE WHEN t.archived_at IS NULL THEN 0 ELSE 1 END AS bit) AS IsArchived,
       t.created_at AS CreatedAt, t.updated_at AS UpdatedAt
FROM dbo.nh_hr_offboarding_templates t
LEFT JOIN dbo.nh_hr_departments d ON d.id = t.department_id AND d.organization_id = t.organization_id
LEFT JOIN dbo.nh_hr_job_titles jt ON jt.id = t.job_title_id AND jt.organization_id = t.organization_id
LEFT JOIN dbo.nh_hr_employment_types et ON et.id = t.employment_type_id AND et.organization_id = t.organization_id";

    public async Task<IReadOnlyList<OffboardingTemplateListItemDto>> ListTemplatesAsync(
        Guid organizationId, OffboardingTemplateListQuery query, CancellationToken cancellationToken = default)
    {
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : "%" + EscapeLike(query.Search.Trim()) + "%";
        var sql = TemplateSelect + @"
WHERE t.organization_id = @OrganizationId
  AND (@IncludeArchived = 1 OR t.archived_at IS NULL)
  AND (@IsActive IS NULL OR t.is_active = @IsActive)
  AND (@DepartmentId IS NULL OR t.department_id = @DepartmentId)
  AND (@SeparationType IS NULL OR t.separation_type = @SeparationType)
  AND (@SearchLike IS NULL OR t.name LIKE @SearchLike ESCAPE '\' OR ISNULL(t.description, '') LIKE @SearchLike ESCAPE '\')
ORDER BY t.is_default DESC, t.name;";

        using var connection = OpenConnection();
        return (await connection.QueryAsync<OffboardingTemplateListItemDto>(new CommandDefinition(sql, new
        {
            OrganizationId = organizationId,
            IncludeArchived = query.IncludeArchived ? 1 : 0,
            query.IsActive,
            query.DepartmentId,
            SeparationType = OffboardingValidation.NormalizeSeparationType(query.SeparationType),
            SearchLike = search
        }, cancellationToken: cancellationToken))).ToList();
    }

    public async Task<OffboardingTemplateDetailDto?> GetTemplateAsync(Guid organizationId, Guid templateId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        return await GetTemplateCoreAsync(connection, null, organizationId, templateId, cancellationToken);
    }

    public async Task<Guid> CreateTemplateAsync(Guid organizationId, UpsertOffboardingTemplateRequest request, Guid? actorUserId, CancellationToken cancellationToken = default)
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

    public async Task<bool> UpdateTemplateAsync(Guid organizationId, Guid templateId, UpsertOffboardingTemplateRequest request, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        try
        {
            if (request.IsDefault)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE dbo.nh_hr_offboarding_templates SET is_default = 0 WHERE organization_id = @OrganizationId AND id <> @Id AND is_default = 1;",
                    new { OrganizationId = organizationId, Id = templateId }, tx, cancellationToken: cancellationToken));
            }

            var updated = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_offboarding_templates
SET name = @Name, description = @Description, separation_type = @SeparationType, department_id = @DepartmentId,
    job_title_id = @JobTitleId, employment_type_id = @EmploymentTypeId, estimated_duration_days = @EstimatedDurationDays,
    is_active = @IsActive, is_default = @IsDefault, version = version + 1,
    updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND id = @Id AND archived_at IS NULL;",
                new
                {
                    OrganizationId = organizationId, Id = templateId,
                    Name = request.Name.Trim(), Description = Trim(request.Description),
                    SeparationType = OffboardingValidation.NormalizeSeparationType(request.SeparationType),
                    request.DepartmentId, request.JobTitleId, request.EmploymentTypeId, request.EstimatedDurationDays,
                    request.IsActive, request.IsDefault, Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId
                }, tx, cancellationToken: cancellationToken));
            if (updated == 0)
            {
                tx.Rollback();
                return false;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.nh_hr_offboarding_template_tasks WHERE organization_id = @OrganizationId AND template_id = @Id;",
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

        var request = new UpsertOffboardingTemplateRequest
        {
            Name = newName,
            Description = source.Description,
            SeparationType = source.SeparationType,
            DepartmentId = source.DepartmentId,
            JobTitleId = source.JobTitleId,
            EmploymentTypeId = source.EmploymentTypeId,
            EstimatedDurationDays = source.EstimatedDurationDays,
            IsActive = true,
            IsDefault = false,
            Tasks = source.Tasks.Select(t => new UpsertOffboardingTemplateTaskRequest
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
UPDATE dbo.nh_hr_offboarding_templates
SET archived_at = @Now, archived_by = @ActorUserId, is_active = 0, is_default = 0, updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND id = @Id AND archived_at IS NULL;";

        using var connection = OpenConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(sql,
            new { OrganizationId = organizationId, Id = templateId, Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId },
            cancellationToken: cancellationToken));
        return affected > 0;
    }

    public async Task<bool> SetDefaultTemplateAsync(Guid organizationId, Guid templateId, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        try
        {
            var exists = await connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT CASE WHEN EXISTS (
    SELECT 1 FROM dbo.nh_hr_offboarding_templates
    WHERE organization_id = @OrganizationId AND id = @Id AND archived_at IS NULL AND is_active = 1
) THEN 1 ELSE 0 END;",
                new { OrganizationId = organizationId, Id = templateId }, tx, cancellationToken: cancellationToken)) == 1;
            if (!exists)
            {
                tx.Rollback();
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_offboarding_templates SET is_default = 0, updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND id <> @Id AND is_default = 1;
UPDATE dbo.nh_hr_offboarding_templates SET is_default = 1, updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND id = @Id;",
                new { OrganizationId = organizationId, Id = templateId, Now = now, ActorUserId = actorUserId },
                tx, cancellationToken: cancellationToken));

            tx.Commit();
            return true;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<bool> TemplateNameExistsAsync(Guid organizationId, string name, Guid? excludeId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT CASE WHEN EXISTS (
    SELECT 1 FROM dbo.nh_hr_offboarding_templates
    WHERE organization_id = @OrganizationId AND archived_at IS NULL AND name = @Name
      AND (@ExcludeId IS NULL OR id <> @ExcludeId)
) THEN 1 ELSE 0 END;";

        using var connection = OpenConnection();
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql,
            new { OrganizationId = organizationId, Name = name.Trim(), ExcludeId = excludeId }, cancellationToken: cancellationToken)) == 1;
    }

    public async Task<string?> CheckTemplateReferencesAsync(Guid organizationId, UpsertOffboardingTemplateRequest request, CancellationToken cancellationToken = default)
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

    public async Task<Guid?> ResolveTemplateIdForEmployeeAsync(Guid organizationId, Guid employeeId, string separationType, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        return await ResolveTemplateIdCoreAsync(connection, null, organizationId, employeeId, separationType, cancellationToken);
    }

    private static async Task<Guid?> ResolveTemplateIdCoreAsync(
        IDbConnection connection, IDbTransaction? tx, Guid organizationId, Guid employeeId, string separationType, CancellationToken cancellationToken)
    {
        // Specific job title wins, then department, then an exact separation-type template, then the separation "family"
        // (e.g. Layoff -> Involuntary Termination), then the org default.
        const string sql = @"
SELECT TOP 1 t.id
FROM dbo.nh_hr_offboarding_templates t
INNER JOIN dbo.nh_hr_employees e ON e.organization_id = t.organization_id AND e.id = @EmployeeId
WHERE t.organization_id = @OrganizationId AND t.archived_at IS NULL AND t.is_active = 1
  AND (t.is_default = 1 OR t.separation_type IS NULL OR t.separation_type IN (@SeparationType, @FamilyType))
  AND (
        (t.job_title_id IS NOT NULL AND t.job_title_id = e.job_title_id)
        OR (t.department_id IS NOT NULL AND t.department_id = e.department_id)
        OR (t.separation_type IS NOT NULL AND t.separation_type IN (@SeparationType, @FamilyType))
        OR t.is_default = 1
      )
ORDER BY
    CASE
        WHEN t.job_title_id IS NOT NULL AND t.job_title_id = e.job_title_id THEN 0
        WHEN t.department_id IS NOT NULL AND t.department_id = e.department_id THEN 1
        WHEN t.separation_type = @SeparationType THEN 2
        WHEN t.separation_type = @FamilyType THEN 3
        ELSE 4
    END,
    t.name;";

        return await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(sql,
            new
            {
                OrganizationId = organizationId, EmployeeId = employeeId, SeparationType = separationType,
                FamilyType = TemplateFamilyFor(separationType)
            }, tx, cancellationToken: cancellationToken));
    }

    /// <summary>Which separation type's template best fits the given type when no exact template exists.</summary>
    internal static string TemplateFamilyFor(string? separationType) => separationType switch
    {
        SeparationTypes.Layoff => SeparationTypes.InvoluntaryTermination,
        SeparationTypes.Retirement or SeparationTypes.MutualAgreement => SeparationTypes.VoluntaryResignation,
        { } other when SeparationTypes.All.Contains(other) => other,
        _ => SeparationTypes.VoluntaryResignation
    };

    private static async Task<OffboardingTemplateDetailDto?> GetTemplateCoreAsync(
        IDbConnection connection, IDbTransaction? tx, Guid organizationId, Guid templateId, CancellationToken cancellationToken)
    {
        var template = await connection.QuerySingleOrDefaultAsync<OffboardingTemplateDetailDto>(new CommandDefinition(
            TemplateSelect + " WHERE t.organization_id = @OrganizationId AND t.id = @Id;",
            new { OrganizationId = organizationId, Id = templateId }, tx, cancellationToken: cancellationToken));
        if (template == null) return null;

        template.Tasks = (await connection.QueryAsync<OffboardingTemplateTaskDto>(new CommandDefinition(@"
SELECT id AS Id, title AS Title, description AS Description, category AS Category, display_order AS DisplayOrder,
       assignment_type AS AssignmentType, default_assignee_user_id AS DefaultAssigneeUserId,
       due_offset_days AS DueOffsetDays, is_required AS IsRequired, requires_document AS RequiresDocument,
       document_category AS DocumentCategory, requires_approval AS RequiresApproval, depends_on_task_id AS DependsOnTaskId
FROM dbo.nh_hr_offboarding_template_tasks
WHERE organization_id = @OrganizationId AND template_id = @Id
ORDER BY display_order, created_at;",
            new { OrganizationId = organizationId, Id = templateId }, tx, cancellationToken: cancellationToken))).ToList();
        return template;
    }

    private static async Task<Guid> InsertTemplateCoreAsync(
        IDbConnection connection, IDbTransaction tx, Guid organizationId,
        UpsertOffboardingTemplateRequest request, Guid? actorUserId, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        if (request.IsDefault)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.nh_hr_offboarding_templates SET is_default = 0 WHERE organization_id = @OrganizationId AND is_default = 1;",
                new { OrganizationId = organizationId }, tx, cancellationToken: cancellationToken));
        }

        await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_offboarding_templates
    (id, organization_id, name, description, separation_type, department_id, job_title_id, employment_type_id,
     estimated_duration_days, is_active, is_default, version, created_at, created_by)
VALUES
    (@Id, @OrganizationId, @Name, @Description, @SeparationType, @DepartmentId, @JobTitleId, @EmploymentTypeId,
     @EstimatedDurationDays, @IsActive, @IsDefault, 1, @Now, @ActorUserId);",
            new
            {
                Id = id, OrganizationId = organizationId,
                Name = request.Name.Trim(), Description = Trim(request.Description),
                SeparationType = OffboardingValidation.NormalizeSeparationType(request.SeparationType),
                request.DepartmentId, request.JobTitleId, request.EmploymentTypeId, request.EstimatedDurationDays,
                request.IsActive, request.IsDefault, Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId
            }, tx, cancellationToken: cancellationToken));

        await InsertTemplateTasksCoreAsync(connection, tx, organizationId, id, request.Tasks, cancellationToken);
        return id;
    }

    /// <summary>Inserts tasks with fresh ids, remapping dependencies expressed through the payload's client ids.</summary>
    private static async Task InsertTemplateTasksCoreAsync(
        IDbConnection connection, IDbTransaction tx, Guid organizationId, Guid templateId,
        IReadOnlyList<UpsertOffboardingTemplateTaskRequest>? tasks, CancellationToken cancellationToken)
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
INSERT INTO dbo.nh_hr_offboarding_template_tasks
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
                Category = OffboardingValidation.NormalizeCategory(t.Category) ?? OffboardingTaskCategories.Other,
                DisplayOrder = i + 1,
                AssignmentType = OffboardingValidation.NormalizeAssignmentType(t.AssignmentType) ?? OffboardingAssignmentTypes.HR,
                t.DefaultAssigneeUserId, t.DueOffsetDays, t.IsRequired, t.RequiresDocument,
                DocumentCategory = Trim(t.DocumentCategory),
                t.RequiresApproval, DependsOnTaskId = dependsOn, Now = now
            }, tx, cancellationToken: cancellationToken));
        }
    }

    // -------------------------------------------------------------------------
    // Offboardings: read
    // -------------------------------------------------------------------------

    private static object BuildFilterArgs(
        Guid organizationId, string? status, Guid? departmentId, Guid? ownerUserId, Guid? templateId, string? separationType,
        DateTime? lastWorkingFrom, DateTime? lastWorkingTo, bool overdueOnly, string? search, DateTime today) => new
    {
        OrganizationId = organizationId,
        Status = string.IsNullOrWhiteSpace(status) ? null : status.Trim(),
        DepartmentId = departmentId,
        OwnerUserId = ownerUserId,
        TemplateId = templateId,
        SeparationType = OffboardingValidation.NormalizeSeparationType(separationType),
        LastWorkingFrom = lastWorkingFrom?.Date,
        LastWorkingTo = lastWorkingTo?.Date,
        OverdueOnly = overdueOnly ? 1 : 0,
        Today = today,
        SearchLike = string.IsNullOrWhiteSpace(search) ? null : "%" + EscapeLike(search.Trim()) + "%"
    };

    public async Task<PagedResult<OffboardingListItemDto>> ListAsync(Guid organizationId, OffboardingListQuery query, CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var today = DateTime.UtcNow.Date;

        var parameters = new DynamicParameters(BuildFilterArgs(
            organizationId, query.Status, query.DepartmentId, query.OwnerUserId, query.TemplateId, query.SeparationType,
            query.LastWorkingFrom, query.LastWorkingTo, query.OverdueOnly, query.Search, today));
        parameters.Add("Offset", (page - 1) * pageSize);
        parameters.Add("PageSize", pageSize);

        var countSql = "SELECT COUNT(1) " + OffboardingJoins + OffboardingFilters + ";";
        var pageSql = OffboardingListSelect + OffboardingJoins + OffboardingFilters + $@"
ORDER BY {BuildOrderBy(query.Sort, query.Dir)}, o.id
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";

        using var connection = OpenConnection();
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(countSql, parameters, cancellationToken: cancellationToken));
        var items = (await connection.QueryAsync<OffboardingListItemDto>(new CommandDefinition(pageSql, parameters, cancellationToken: cancellationToken))).ToList();
        await AttachProgressAsync(connection, organizationId, items, today, cancellationToken);

        return new PagedResult<OffboardingListItemDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            Total = total
        };
    }

    public async Task<OffboardingPipelineDto> PipelineAsync(Guid organizationId, OffboardingPipelineQuery query, CancellationToken cancellationToken = default)
    {
        const int cap = 1000;
        var today = DateTime.UtcNow.Date;
        var completedSince = today.AddDays(-Math.Clamp(query.CompletedWithinDays, 1, 3650));

        var parameters = new DynamicParameters(BuildFilterArgs(
            organizationId, null, query.DepartmentId, query.OwnerUserId, query.TemplateId, query.SeparationType,
            query.LastWorkingFrom, query.LastWorkingTo, query.OverdueOnly, query.Search, today));
        parameters.Add("CompletedSince", completedSince);
        parameters.Add("Cap", cap);

        // Cancelled offboardings never appear on the board.
        var sql = "SELECT TOP (@Cap) * FROM (" + OffboardingListSelect + OffboardingJoins + OffboardingFilters + @"
  AND o.status IN ('Scheduled', 'InProgress', 'PendingClearance', 'Completed')
  AND (o.status <> 'Completed' OR o.actual_completion_date IS NULL OR o.actual_completion_date >= @CompletedSince)
) AS p
ORDER BY CASE WHEN p.LastWorkingDay IS NULL THEN 1 ELSE 0 END, p.LastWorkingDay, p.EmployeeName, p.Id;";

        using var connection = OpenConnection();
        var items = (await connection.QueryAsync<OffboardingListItemDto>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))).ToList();
        await AttachProgressAsync(connection, organizationId, items, today, cancellationToken);

        var pipeline = new OffboardingPipelineDto();
        foreach (var (statusId, label) in OffboardingStatuses.PipelineColumns)
        {
            var cards = items.Where(i => i.Status.Equals(statusId, StringComparison.OrdinalIgnoreCase))
                .Select(i => new OffboardingPipelineCardDto
                {
                    Id = i.Id,
                    EmployeeId = i.EmployeeId,
                    EmployeeName = i.EmployeeName,
                    JobTitle = i.JobTitle,
                    Department = i.Department,
                    AvatarUrl = i.AvatarUrl,
                    SeparationType = i.SeparationType,
                    LastWorkingDay = i.LastWorkingDay,
                    ExpectedCompletion = i.ExpectedCompletionDate,
                    ProgressPercent = i.ProgressPercent,
                    OverdueCount = i.OverdueCount,
                    PendingAssets = i.PendingAssets,
                    PendingAccessItems = i.PendingAccessItems,
                    OwnerName = i.OwnerName,
                    TemplateName = i.TemplateName
                }).ToList();
            pipeline.Columns.Add(new OffboardingPipelineColumnDto { Status = statusId, Label = label, Count = cards.Count, Cards = cards });
        }
        return pipeline;
    }

    public async Task<OffboardingDetailDto?> GetAsync(Guid organizationId, Guid offboardingId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var detail = await connection.QuerySingleOrDefaultAsync<OffboardingDetailDto>(new CommandDefinition(
            OffboardingDetailSelect + OffboardingJoins + " WHERE o.organization_id = @OrganizationId AND o.id = @Id;",
            new { OrganizationId = organizationId, Id = offboardingId }, cancellationToken: cancellationToken));
        if (detail == null) return null;

        var today = DateTime.UtcNow.Date;
        var args = new { OrganizationId = organizationId, OffboardingId = offboardingId };

        var tasks = await ListTasksCoreAsync(connection, organizationId, offboardingId, today, cancellationToken);
        detail.Tasks = tasks.ToList();
        detail.Assets = (await connection.QueryAsync<OffboardingAssetDto>(new CommandDefinition(
            AssetSelect + " WHERE organization_id = @OrganizationId AND offboarding_id = @OffboardingId ORDER BY created_at, asset_name;",
            args, cancellationToken: cancellationToken))).ToList();
        detail.AccessItems = (await connection.QueryAsync<OffboardingAccessItemDto>(new CommandDefinition(
            AccessSelect + " WHERE x.organization_id = @OrganizationId AND x.offboarding_id = @OffboardingId ORDER BY x.created_at, x.system_name;",
            args, cancellationToken: cancellationToken))).ToList();
        detail.SettlementItems = (await connection.QueryAsync<OffboardingSettlementItemDto>(new CommandDefinition(
            SettlementSelect + " WHERE organization_id = @OrganizationId AND offboarding_id = @OffboardingId ORDER BY created_at, title;",
            args, cancellationToken: cancellationToken))).ToList();
        detail.ExitInterview = await connection.QuerySingleOrDefaultAsync<OffboardingExitInterviewDto>(new CommandDefinition(
            ExitInterviewSelect + " WHERE x.organization_id = @OrganizationId AND x.offboarding_id = @OffboardingId;",
            args, cancellationToken: cancellationToken));
        detail.Documents = (await connection.QueryAsync<OffboardingDocumentDto>(new CommandDefinition(
            DocumentSelect + " WHERE organization_id = @OrganizationId AND offboarding_id = @OffboardingId ORDER BY created_at, name;",
            args, cancellationToken: cancellationToken))).ToList();

        ApplyProgress(detail, tasks, today);
        var clearanceBlockers = OffboardingValidation.GetClearanceBlockers(tasks);
        detail.ClearanceBlockers = clearanceBlockers.Select(t => t.Title).ToList();
        detail.CanSubmitClearance = clearanceBlockers.Count == 0;
        detail.CompletionBlockers = OffboardingValidation.GetCompletionBlockerLabels(tasks, detail.Assets, detail.AccessItems, detail.SettlementItems);
        detail.CanComplete = detail.CompletionBlockers.Count == 0;
        return detail;
    }

    public async Task<OffboardingHeader?> GetHeaderAsync(Guid organizationId, Guid offboardingId, CancellationToken cancellationToken = default)
    {
        var sql = @"
SELECT o.id AS Id, o.employee_id AS EmployeeId, " + EmployeeNameSql + @" AS EmployeeName, o.status AS Status,
       o.separation_type AS SeparationType, o.notice_date AS NoticeDate, o.process_start_date AS ProcessStartDate,
       o.last_working_day AS LastWorkingDay, o.termination_effective_date AS TerminationEffectiveDate,
       o.expected_completion_date AS ExpectedCompletionDate, o.owner_user_id AS OwnerUserId, o.template_id AS TemplateId,
       o.notify_employee AS NotifyEmployee, o.employment_status_applied AS EmploymentStatusApplied
FROM dbo.nh_hr_employee_offboardings o
INNER JOIN dbo.nh_hr_employees e ON e.id = o.employee_id AND e.organization_id = o.organization_id
WHERE o.organization_id = @OrganizationId AND o.id = @Id;";

        using var connection = OpenConnection();
        return await connection.QuerySingleOrDefaultAsync<OffboardingHeader>(new CommandDefinition(sql,
            new { OrganizationId = organizationId, Id = offboardingId }, cancellationToken: cancellationToken));
    }

    public async Task<OffboardingEmployeeInfo?> GetEmployeeInfoAsync(Guid organizationId, Guid employeeId, CancellationToken cancellationToken = default)
    {
        var sql = @"
SELECT e.id AS Id, " + EmployeeNameSql + @" AS DisplayName,
       CAST(CASE WHEN e.archived_at IS NULL THEN 0 ELSE 1 END AS bit) AS IsArchived,
       e.employment_status AS EmploymentStatus,
       e.application_id AS ApplicationId, e.hire_date AS HireDate
FROM dbo.nh_hr_employees e
WHERE e.organization_id = @OrganizationId AND e.id = @EmployeeId;";

        using var connection = OpenConnection();
        return await connection.QuerySingleOrDefaultAsync<OffboardingEmployeeInfo>(new CommandDefinition(sql,
            new { OrganizationId = organizationId, EmployeeId = employeeId }, cancellationToken: cancellationToken));
    }

    public async Task<Guid?> FindActiveOffboardingIdAsync(Guid organizationId, Guid employeeId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT TOP 1 id FROM dbo.nh_hr_employee_offboardings
WHERE organization_id = @OrganizationId AND employee_id = @EmployeeId
  AND status IN ('Scheduled', 'InProgress', 'PendingClearance');";

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
    // Offboardings: create / update / transitions
    // -------------------------------------------------------------------------

    public async Task<CreatedOffboarding> CreateOffboardingAsync(Guid organizationId, CreateOffboardingCommand command, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        try
        {
            var created = await InsertOffboardingCoreAsync(connection, tx, organizationId, command, cancellationToken);
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
        public string Category { get; set; } = OffboardingTaskCategories.Other;
        public int DisplayOrder { get; set; }
        public string AssignmentType { get; set; } = OffboardingAssignmentTypes.HR;
        public Guid? DefaultAssigneeUserId { get; set; }
        public int? DueOffsetDays { get; set; }
        public bool IsRequired { get; set; }
        public bool RequiresDocument { get; set; }
        public string? DocumentCategory { get; set; }
        public bool RequiresApproval { get; set; }
        public Guid? DependsOnTaskId { get; set; }
    }

    private async Task<CreatedOffboarding> InsertOffboardingCoreAsync(
        IDbConnection connection, IDbTransaction tx, Guid organizationId, CreateOffboardingCommand cmd, CancellationToken cancellationToken)
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
FROM dbo.nh_hr_offboarding_templates
WHERE organization_id = @OrganizationId AND id = @Id AND archived_at IS NULL;",
                new { OrganizationId = organizationId, Id = cmd.TemplateId.Value }, tx, cancellationToken: cancellationToken))
                ?? throw new InvalidOperationException("Template not found.");

            templateTasks = (await connection.QueryAsync<TemplateTaskRow>(new CommandDefinition(@"
SELECT id AS Id, title AS Title, description AS Description, category AS Category, display_order AS DisplayOrder,
       assignment_type AS AssignmentType, default_assignee_user_id AS DefaultAssigneeUserId,
       due_offset_days AS DueOffsetDays, is_required AS IsRequired, requires_document AS RequiresDocument,
       document_category AS DocumentCategory, requires_approval AS RequiresApproval, depends_on_task_id AS DependsOnTaskId
FROM dbo.nh_hr_offboarding_template_tasks
WHERE organization_id = @OrganizationId AND template_id = @Id
ORDER BY display_order, created_at;",
                new { OrganizationId = organizationId, Id = cmd.TemplateId.Value }, tx, cancellationToken: cancellationToken))).ToList();
        }

        var excluded = new HashSet<Guid>(cmd.ExcludedTemplateTaskIds);
        var selected = templateTasks.Where(t => !excluded.Contains(t.Id)).ToList();

        var offboardingId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var today = DateTime.UtcNow.Date;
        var lastWorkingDay = cmd.LastWorkingDay?.Date;
        var terminationEffective = OffboardingValidation.ResolveTerminationEffectiveDate(cmd.TerminationEffectiveDate, lastWorkingDay);

        // Expected completion defaults to the latest due date of the required tasks, then the last working day.
        var requiredDueDates = selected.Where(t => t.IsRequired)
            .Select(t => OffboardingValidation.ComputeDueDate(lastWorkingDay, t.DueOffsetDays))
            .Where(d => d.HasValue).Select(d => d!.Value).ToList();
        DateTime? expected = cmd.ExpectedCompletionDate?.Date
                             ?? (requiredDueDates.Count > 0 ? requiredDueDates.Max() : lastWorkingDay);

        var startsNow = cmd.InitialStatus == OffboardingStatuses.InProgress;

        await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_employee_offboardings
    (id, organization_id, employee_id, template_id, template_version, owner_user_id, separation_type, separation_reason,
     notice_date, process_start_date, last_working_day, termination_effective_date, expected_completion_date,
     rehire_eligibility, confidential_notes, notify_employee, status, employment_status_applied, created_at, created_by)
VALUES
    (@Id, @OrganizationId, @EmployeeId, @TemplateId, @TemplateVersion, @OwnerUserId, @SeparationType, @SeparationReason,
     @NoticeDate, @ProcessStartDate, @LastWorkingDay, @TerminationEffectiveDate, @ExpectedCompletionDate,
     @RehireEligibility, @ConfidentialNotes, @NotifyEmployee, @Status, 0, @Now, @ActorUserId);",
            new
            {
                Id = offboardingId, OrganizationId = organizationId, cmd.EmployeeId,
                TemplateId = template?.Id, TemplateVersion = template?.Version,
                cmd.OwnerUserId, cmd.SeparationType, SeparationReason = Trim(cmd.SeparationReason),
                NoticeDate = cmd.NoticeDate?.Date, ProcessStartDate = startsNow ? today : (DateTime?)null,
                LastWorkingDay = lastWorkingDay, TerminationEffectiveDate = terminationEffective,
                ExpectedCompletionDate = expected,
                RehireEligibility = OffboardingValidation.NormalizeRehireEligibility(cmd.RehireEligibility),
                ConfidentialNotes = Trim(cmd.ConfidentialNotes),
                NotifyEmployee = OffboardingValidation.CanNotifyEmployee(cmd.SeparationType, cmd.NotifyEmployee),
                Status = cmd.InitialStatus, Now = now, ActorUserId = cmd.ActorUserId
            }, tx, cancellationToken: cancellationToken));

        // Snapshot template tasks (due date = last working day + offset), remapping dependencies.
        var idMap = selected.ToDictionary(t => t.Id, _ => Guid.NewGuid());
        var taskIds = new List<Guid>();
        var order = 0;
        foreach (var tt in selected)
        {
            order++;
            Guid? assigned = tt.AssignmentType switch
            {
                OffboardingAssignmentTypes.SpecificUser => tt.DefaultAssigneeUserId,
                OffboardingAssignmentTypes.HR => tt.DefaultAssigneeUserId ?? cmd.OwnerUserId,
                OffboardingAssignmentTypes.IT => tt.DefaultAssigneeUserId,
                OffboardingAssignmentTypes.Finance => tt.DefaultAssigneeUserId,
                OffboardingAssignmentTypes.DirectManager => employeeLinks.ManagerUserId ?? tt.DefaultAssigneeUserId,
                OffboardingAssignmentTypes.Employee => employeeLinks.LinkedUserId,
                _ => tt.DefaultAssigneeUserId
            };

            var taskId = idMap[tt.Id];
            taskIds.Add(taskId);
            await InsertTaskCoreAsync(connection, tx, organizationId, offboardingId, taskId, new OffboardingTaskSpec
            {
                Title = tt.Title,
                Description = tt.Description,
                Category = tt.Category,
                AssignmentType = tt.AssignmentType,
                AssignedUserId = assigned,
                DueDate = OffboardingValidation.ComputeDueDate(lastWorkingDay, tt.DueOffsetDays),
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
            await InsertTaskCoreAsync(connection, tx, organizationId, offboardingId, taskId, extra, null, order, now, cancellationToken);
        }

        foreach (var asset in cmd.Assets)
            await InsertAssetCoreAsync(connection, tx, organizationId, offboardingId, asset, now, cancellationToken);

        var accessSystems = (cmd.AccessSystems ?? OffboardingDefaults.AccessSystems)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var system in accessSystems)
        {
            await InsertAccessItemCoreAsync(connection, tx, organizationId, offboardingId,
                new OffboardingAccessSpec { SystemName = system }, now, cancellationToken);
        }

        foreach (var item in OffboardingDefaults.Settlement)
        {
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_offboarding_settlement_items
    (id, organization_id, offboarding_id, item_code, title, is_required, status, created_at)
VALUES
    (@Id, @OrganizationId, @OffboardingId, @ItemCode, @Title, @IsRequired, 'Pending', @Now);",
                new
                {
                    Id = Guid.NewGuid(), OrganizationId = organizationId, OffboardingId = offboardingId,
                    ItemCode = item.Code, item.Title, IsRequired = item.Required, Now = now
                }, tx, cancellationToken: cancellationToken));
        }

        await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_offboarding_exit_interviews
    (id, organization_id, offboarding_id, status, questionnaire_json, created_at)
VALUES
    (@Id, @OrganizationId, @OffboardingId, 'NotScheduled', @Questionnaire, @Now);",
            new
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, OffboardingId = offboardingId,
                Questionnaire = OffboardingDefaults.ExitQuestionnaireJson, Now = now
            }, tx, cancellationToken: cancellationToken));

        // Activity details never include the (confidential) separation reason or notes.
        await InsertActivityCoreAsync(connection, tx, organizationId, offboardingId, "Created", "Offboarding created",
            JsonSerializer.Serialize(new
            {
                templateId = template?.Id, taskCount = taskIds.Count, separationType = cmd.SeparationType,
                lastWorkingDay = lastWorkingDay?.ToString("yyyy-MM-dd")
            }),
            cmd.ActorUserId, cmd.ActorName, cancellationToken);
        if (startsNow)
        {
            await InsertActivityCoreAsync(connection, tx, organizationId, offboardingId, "StatusChanged",
                "Offboarding started", JsonSerializer.Serialize(new { from = OffboardingStatuses.Scheduled, to = OffboardingStatuses.InProgress }),
                cmd.ActorUserId, cmd.ActorName, cancellationToken);
        }

        return new CreatedOffboarding(offboardingId, taskIds);
    }

    private static async Task InsertTaskCoreAsync(
        IDbConnection connection, IDbTransaction tx, Guid organizationId, Guid offboardingId, Guid taskId,
        OffboardingTaskSpec spec, Guid? templateTaskId, int displayOrder, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var category = OffboardingValidation.NormalizeCategory(spec.Category) ?? OffboardingTaskCategories.Other;
        var assignment = OffboardingValidation.NormalizeAssignmentType(spec.AssignmentType) ?? OffboardingAssignmentTypes.HR;
        var docCategory = Trim(spec.DocumentCategory);

        await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_offboarding_tasks
    (id, organization_id, offboarding_id, template_task_id, title, description, category, assignment_type,
     assigned_user_id, due_date, status, is_required, requires_document, document_category, requires_approval,
     display_order, depends_on_task_id, is_waived, created_at)
VALUES
    (@Id, @OrganizationId, @OffboardingId, @TemplateTaskId, @Title, @Description, @Category, @AssignmentType,
     @AssignedUserId, @DueDate, 'Pending', @IsRequired, @RequiresDocument, @DocumentCategory, @RequiresApproval,
     @DisplayOrder, @DependsOnTaskId, 0, @Now);",
            new
            {
                Id = taskId, OrganizationId = organizationId, OffboardingId = offboardingId, TemplateTaskId = templateTaskId,
                Title = spec.Title.Trim(), Description = Trim(spec.Description), Category = category, AssignmentType = assignment,
                spec.AssignedUserId, DueDate = spec.DueDate?.Date, spec.IsRequired, spec.RequiresDocument,
                DocumentCategory = docCategory, spec.RequiresApproval, DisplayOrder = displayOrder,
                spec.DependsOnTaskId, Now = now
            }, tx, cancellationToken: cancellationToken));

        if (spec.RequiresDocument)
            await InsertDocumentRequirementCoreAsync(connection, tx, organizationId, offboardingId, taskId, spec, now, cancellationToken);
    }

    private static Task InsertDocumentRequirementCoreAsync(
        IDbConnection connection, IDbTransaction? tx, Guid organizationId, Guid offboardingId, Guid taskId,
        OffboardingTaskSpec spec, DateTimeOffset now, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_offboarding_documents
    (id, organization_id, offboarding_id, offboarding_task_id, category, name, is_required, status, created_at)
VALUES
    (@Id, @OrganizationId, @OffboardingId, @TaskId, @Category, @Name, @IsRequired, 'Missing', @Now);",
            new
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, OffboardingId = offboardingId, TaskId = taskId,
                Category = Trim(spec.DocumentCategory) ?? OffboardingTaskCategories.Other,
                Name = spec.Title.Trim().Length > 300 ? spec.Title.Trim()[..300] : spec.Title.Trim(),
                spec.IsRequired, Now = now
            }, tx, cancellationToken: cancellationToken));

    private static Task InsertAssetCoreAsync(
        IDbConnection connection, IDbTransaction? tx, Guid organizationId, Guid offboardingId,
        OffboardingAssetSpec asset, DateTimeOffset now, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_offboarding_assets
    (id, organization_id, offboarding_id, asset_name, asset_type, serial_number, return_required,
     expected_return_date, status, notes, created_at)
VALUES
    (@Id, @OrganizationId, @OffboardingId, @AssetName, @AssetType, @SerialNumber, @ReturnRequired,
     @ExpectedReturnDate, 'PendingReturn', @Notes, @Now);",
            new
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, OffboardingId = offboardingId,
                AssetName = asset.AssetName.Trim(), AssetType = Trim(asset.AssetType), SerialNumber = Trim(asset.SerialNumber),
                asset.ReturnRequired, ExpectedReturnDate = asset.ExpectedReturnDate?.Date, Notes = Trim(asset.Notes), Now = now
            }, tx, cancellationToken: cancellationToken));

    private static Task InsertAccessItemCoreAsync(
        IDbConnection connection, IDbTransaction? tx, Guid organizationId, Guid offboardingId,
        OffboardingAccessSpec item, DateTimeOffset now, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_offboarding_access_items
    (id, organization_id, offboarding_id, system_name, responsible_user_id, scheduled_at, status, notes, created_at)
VALUES
    (@Id, @OrganizationId, @OffboardingId, @SystemName, @ResponsibleUserId, @ScheduledAt,
     CASE WHEN @ScheduledAt IS NULL THEN 'Pending' ELSE 'Scheduled' END, @Notes, @Now);",
            new
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, OffboardingId = offboardingId,
                SystemName = item.SystemName.Trim(), item.ResponsibleUserId, item.ScheduledAt,
                Notes = Trim(item.Notes), Now = now
            }, tx, cancellationToken: cancellationToken));

    public async Task<bool> UpdateAsync(Guid organizationId, Guid offboardingId, UpdateOffboardingCommand command, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        try
        {
            var previousLastDay = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                "SELECT last_working_day FROM dbo.nh_hr_employee_offboardings WHERE organization_id = @OrganizationId AND id = @Id;",
                new { OrganizationId = organizationId, Id = offboardingId }, tx, cancellationToken: cancellationToken));

            var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_employee_offboardings
SET owner_user_id = @OwnerUserId, separation_type = @SeparationType,
    separation_reason = CASE WHEN @UpdateConfidential = 1 THEN @SeparationReason ELSE separation_reason END,
    confidential_notes = CASE WHEN @UpdateConfidential = 1 THEN @ConfidentialNotes ELSE confidential_notes END,
    notice_date = @NoticeDate, last_working_day = @LastWorkingDay, termination_effective_date = @TerminationEffectiveDate,
    expected_completion_date = @ExpectedCompletionDate, rehire_eligibility = @RehireEligibility,
    notify_employee = @NotifyEmployee, updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND id = @Id;",
                new
                {
                    OrganizationId = organizationId, Id = offboardingId, command.OwnerUserId, command.SeparationType,
                    command.SeparationReason, command.ConfidentialNotes, UpdateConfidential = command.UpdateConfidential ? 1 : 0,
                    NoticeDate = command.NoticeDate?.Date, LastWorkingDay = command.LastWorkingDay?.Date,
                    TerminationEffectiveDate = command.TerminationEffectiveDate?.Date,
                    ExpectedCompletionDate = command.ExpectedCompletionDate?.Date,
                    RehireEligibility = OffboardingValidation.NormalizeRehireEligibility(command.RehireEligibility),
                    NotifyEmployee = OffboardingValidation.CanNotifyEmployee(command.SeparationType, command.NotifyEmployee),
                    Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId
                }, tx, cancellationToken: cancellationToken));
            if (affected == 0)
            {
                tx.Rollback();
                return false;
            }

            if (previousLastDay == null && command.LastWorkingDay.HasValue)
                await BackfillDueDatesAsync(connection, tx, organizationId, offboardingId, command.LastWorkingDay.Value.Date, cancellationToken);

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
        Guid organizationId, Guid offboardingId, string from, string to,
        DateTime? completionDate, string? cancelReason, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var today = DateTime.UtcNow.Date;
        var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_employee_offboardings
SET status = @To,
    process_start_date = CASE WHEN @To = 'InProgress' AND process_start_date IS NULL THEN @Today ELSE process_start_date END,
    actual_completion_date = CASE WHEN @To = 'Completed' THEN @CompletionDate
                                  WHEN @To IN ('InProgress', 'PendingClearance') THEN NULL
                                  ELSE actual_completion_date END,
    termination_effective_date = CASE WHEN @To = 'Completed' AND termination_effective_date IS NULL
                                      THEN COALESCE(last_working_day, @CompletionDate)
                                      ELSE termination_effective_date END,
    cancel_reason = CASE WHEN @To = 'Cancelled' THEN @CancelReason ELSE cancel_reason END,
    updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND id = @Id AND status = @From;",
            new
            {
                OrganizationId = organizationId, Id = offboardingId, From = from, To = to, Today = today,
                CompletionDate = (completionDate ?? today).Date, CancelReason = Trim(cancelReason),
                Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId
            }, cancellationToken: cancellationToken));
        return affected > 0;
    }

    /// <summary>When an offboarding gets its last working day late, template tasks get last working day + offset.</summary>
    private static Task BackfillDueDatesAsync(
        IDbConnection connection, IDbTransaction tx, Guid organizationId, Guid offboardingId, DateTime lastWorkingDay, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(@"
UPDATE ot
SET ot.due_date = DATEADD(day, tt.due_offset_days, @LastWorkingDay), ot.updated_at = @Now
FROM dbo.nh_hr_offboarding_tasks ot
INNER JOIN dbo.nh_hr_offboarding_template_tasks tt ON tt.id = ot.template_task_id
WHERE ot.organization_id = @OrganizationId AND ot.offboarding_id = @OffboardingId
  AND ot.due_date IS NULL AND tt.due_offset_days IS NOT NULL;",
            new { OrganizationId = organizationId, OffboardingId = offboardingId, LastWorkingDay = lastWorkingDay.Date, Now = DateTimeOffset.UtcNow },
            tx, cancellationToken: cancellationToken));

    // -------------------------------------------------------------------------
    // Employment status (separation -> employee record)
    // -------------------------------------------------------------------------

    private sealed class DueTerminationRow
    {
        public Guid Id { get; set; }
        public Guid EmployeeId { get; set; }
        public string SeparationType { get; set; } = SeparationTypes.Other;
        public DateTime EffectiveDate { get; set; }
    }

    private sealed class EmployeeSnapshotRow
    {
        public string EmploymentStatus { get; set; } = string.Empty;
        public Guid? JobTitleId { get; set; }
        public Guid? DepartmentId { get; set; }
        public Guid? EmploymentTypeId { get; set; }
        public Guid? WorkLocationId { get; set; }
        public Guid? ManagerId { get; set; }
        public DateTime? TerminationDate { get; set; }
    }

    private const string DueTerminationSelect = @"
SELECT o.id AS Id, o.employee_id AS EmployeeId, o.separation_type AS SeparationType,
       o.termination_effective_date AS EffectiveDate
FROM dbo.nh_hr_employee_offboardings o";

    private const string DueTerminationFilter = @"
WHERE o.organization_id = @OrganizationId
  AND o.employment_status_applied = 0
  AND o.status <> 'Cancelled'
  AND o.termination_effective_date IS NOT NULL
  AND o.termination_effective_date <= @Today";

    public async Task<bool> ApplyEmploymentStatusAsync(
        Guid organizationId, Guid offboardingId, Guid? actorUserId, string? actorName, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var row = await connection.QuerySingleOrDefaultAsync<DueTerminationRow>(new CommandDefinition(
            DueTerminationSelect + DueTerminationFilter + " AND o.id = @Id;",
            new { OrganizationId = organizationId, Id = offboardingId, Today = DateTime.UtcNow.Date }, cancellationToken: cancellationToken));
        if (row == null) return false;
        return await ApplyTerminationAsync(connection, organizationId, row, actorUserId, actorName, cancellationToken);
    }

    public async Task<int> ApplyDueTerminationsAsync(
        Guid organizationId, Guid? actorUserId, string? actorName, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var due = (await connection.QueryAsync<DueTerminationRow>(new CommandDefinition(
            DueTerminationSelect + DueTerminationFilter + " ORDER BY o.termination_effective_date, o.id;",
            new { OrganizationId = organizationId, Today = DateTime.UtcNow.Date }, cancellationToken: cancellationToken))).ToList();

        var applied = 0;
        foreach (var row in due)
        {
            try
            {
                if (await ApplyTerminationAsync(connection, organizationId, row, actorUserId, actorName, cancellationToken)) applied++;
            }
            catch (SqlException ex)
            {
                // One bad row must not block the rest; it will be retried on the next call.
                _logger.LogWarning(ex, "Could not apply employment status for offboarding {OffboardingId}.", row.Id);
            }
        }
        return applied;
    }

    /// <summary>
    /// Applies one due termination atomically. The compare-and-set on employment_status_applied makes concurrent
    /// callers safe: exactly one of them writes the employee changes. Cancelling an offboarding never reverts this.
    /// </summary>
    private async Task<bool> ApplyTerminationAsync(
        IDbConnection connection, Guid organizationId, DueTerminationRow row, Guid? actorUserId, string? actorName, CancellationToken cancellationToken)
    {
        using var tx = connection.BeginTransaction();
        try
        {
            var now = DateTimeOffset.UtcNow;

            var claimed = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_employee_offboardings
SET employment_status_applied = 1, updated_at = @Now
WHERE organization_id = @OrganizationId AND id = @Id AND employment_status_applied = 0 AND status <> 'Cancelled';",
                new { OrganizationId = organizationId, Id = row.Id, Now = now }, tx, cancellationToken: cancellationToken));
            if (claimed == 0)
            {
                tx.Rollback();
                return false;
            }

            var employee = await connection.QuerySingleOrDefaultAsync<EmployeeSnapshotRow>(new CommandDefinition(@"
SELECT employment_status AS EmploymentStatus, job_title_id AS JobTitleId, department_id AS DepartmentId,
       employment_type_id AS EmploymentTypeId, work_location_id AS WorkLocationId, manager_id AS ManagerId,
       termination_date AS TerminationDate
FROM dbo.nh_hr_employees WITH (UPDLOCK)
WHERE organization_id = @OrganizationId AND id = @EmployeeId;",
                new { OrganizationId = organizationId, row.EmployeeId }, tx, cancellationToken: cancellationToken));
            if (employee == null)
            {
                tx.Rollback();
                return false;
            }

            var effective = row.EffectiveDate.Date;
            var reason = SeparationTypes.Label(row.SeparationType);

            if (string.Equals(employee.EmploymentStatus, EmploymentStatuses.Terminated, StringComparison.OrdinalIgnoreCase))
            {
                // Already terminated (e.g. manually): only fill in the missing facts, no duplicate history / activity.
                await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_employees
SET termination_date = COALESCE(termination_date, @Effective),
    termination_reason = COALESCE(termination_reason, @Reason),
    updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND id = @EmployeeId
  AND (termination_date IS NULL OR termination_reason IS NULL);",
                    new { OrganizationId = organizationId, row.EmployeeId, Effective = effective, Reason = reason, Now = now, ActorUserId = actorUserId },
                    tx, cancellationToken: cancellationToken));
            }
            else
            {
                await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_employees
SET employment_status = 'Terminated', termination_date = @Effective, termination_reason = @Reason,
    updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND id = @EmployeeId;",
                    new { OrganizationId = organizationId, row.EmployeeId, Effective = effective, Reason = reason, Now = now, ActorUserId = actorUserId },
                    tx, cancellationToken: cancellationToken));

                await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_employee_employment_history
    (id, organization_id, employee_id, effective_at, change_type, job_title_id, department_id,
     employment_type_id, employment_status, work_location_id, manager_id, notes, created_at, created_by)
VALUES
    (@Id, @OrganizationId, @EmployeeId, @EffectiveAt, 'Termination', @JobTitleId, @DepartmentId,
     @EmploymentTypeId, 'Terminated', @WorkLocationId, @ManagerId, @Notes, @Now, @ActorUserId);",
                    new
                    {
                        Id = Guid.NewGuid(), OrganizationId = organizationId, row.EmployeeId,
                        EffectiveAt = new DateTimeOffset(effective, TimeSpan.Zero),
                        employee.JobTitleId, employee.DepartmentId, employee.EmploymentTypeId,
                        employee.WorkLocationId, employee.ManagerId,
                        Notes = $"Offboarding: {reason}", Now = now, ActorUserId = actorUserId
                    }, tx, cancellationToken: cancellationToken));

                await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_employee_activity
    (id, organization_id, employee_id, activity_type, summary, details_json, created_at, created_by, created_by_name)
VALUES
    (@Id, @OrganizationId, @EmployeeId, 'StatusChanged', @Summary, @DetailsJson, @Now, @ActorUserId, @ActorName);",
                    new
                    {
                        Id = Guid.NewGuid(), OrganizationId = organizationId, row.EmployeeId,
                        Summary = $"Employment status changed from {employee.EmploymentStatus} to Terminated (offboarding)",
                        DetailsJson = JsonSerializer.Serialize(new
                        {
                            from = employee.EmploymentStatus, to = EmploymentStatuses.Terminated,
                            terminationDate = effective.ToString("yyyy-MM-dd"), offboardingId = row.Id
                        }),
                        Now = now, ActorUserId = actorUserId, ActorName = Trim(actorName) ?? "System"
                    }, tx, cancellationToken: cancellationToken));
            }

            await InsertActivityCoreAsync(connection, tx, organizationId, row.Id, "EmploymentStatusApplied",
                $"Employee marked as Terminated effective {effective:yyyy-MM-dd}",
                JsonSerializer.Serialize(new { terminationDate = effective.ToString("yyyy-MM-dd"), previousStatus = employee.EmploymentStatus }),
                actorUserId, Trim(actorName) ?? "System", cancellationToken);

            tx.Commit();
            return true;
        }
        catch
        {
            try { tx.Rollback(); } catch (InvalidOperationException) { /* already completed */ }
            throw;
        }
    }

    // -------------------------------------------------------------------------
    // Tasks
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<OffboardingTaskDto>> ListTasksAsync(Guid organizationId, Guid offboardingId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        return await ListTasksCoreAsync(connection, organizationId, offboardingId, DateTime.UtcNow.Date, cancellationToken);
    }

    private static async Task<IReadOnlyList<OffboardingTaskDto>> ListTasksCoreAsync(
        IDbConnection connection, Guid organizationId, Guid offboardingId, DateTime today, CancellationToken cancellationToken)
    {
        var tasks = (await connection.QueryAsync<OffboardingTaskDto>(new CommandDefinition(
            TaskSelect + " WHERE t.organization_id = @OrganizationId AND t.offboarding_id = @OffboardingId ORDER BY t.display_order, t.created_at;",
            new { OrganizationId = organizationId, OffboardingId = offboardingId }, cancellationToken: cancellationToken))).ToList();
        foreach (var t in tasks) t.IsOverdue = OffboardingValidation.IsOverdue(t, today);
        return tasks;
    }

    public async Task<OffboardingTaskDto?> GetTaskAsync(Guid organizationId, Guid offboardingId, Guid taskId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var task = await connection.QuerySingleOrDefaultAsync<OffboardingTaskDto>(new CommandDefinition(
            TaskSelect + " WHERE t.organization_id = @OrganizationId AND t.offboarding_id = @OffboardingId AND t.id = @Id;",
            new { OrganizationId = organizationId, OffboardingId = offboardingId, Id = taskId }, cancellationToken: cancellationToken));
        if (task != null) task.IsOverdue = OffboardingValidation.IsOverdue(task, DateTime.UtcNow.Date);
        return task;
    }

    public async Task<Guid> AddTaskAsync(Guid organizationId, Guid offboardingId, OffboardingTaskSpec task, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        try
        {
            var order = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT ISNULL(MAX(display_order), 0) + 1 FROM dbo.nh_hr_offboarding_tasks WHERE organization_id = @OrganizationId AND offboarding_id = @OffboardingId;",
                new { OrganizationId = organizationId, OffboardingId = offboardingId }, tx, cancellationToken: cancellationToken));
            var id = Guid.NewGuid();
            await InsertTaskCoreAsync(connection, tx, organizationId, offboardingId, id, task, null, order, DateTimeOffset.UtcNow, cancellationToken);
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
        Guid organizationId, Guid offboardingId, Guid taskId, OffboardingTaskSpec task, string? status, bool? isWaived, string? waiveReason,
        CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var now = DateTimeOffset.UtcNow;
        var category = OffboardingValidation.NormalizeCategory(task.Category) ?? OffboardingTaskCategories.Other;
        var assignment = OffboardingValidation.NormalizeAssignmentType(task.AssignmentType) ?? OffboardingAssignmentTypes.HR;

        var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_offboarding_tasks
SET title = @Title, description = @Description, category = @Category, assignment_type = @AssignmentType,
    assigned_user_id = @AssignedUserId, due_date = @DueDate, is_required = @IsRequired,
    requires_document = @RequiresDocument, document_category = @DocumentCategory,
    requires_approval = @RequiresApproval,
    status = CASE WHEN @Status IS NOT NULL AND status NOT IN ('Completed', 'Cancelled') THEN @Status ELSE status END,
    is_waived = ISNULL(@IsWaived, is_waived),
    waive_reason = CASE WHEN @IsWaived = 1 THEN @WaiveReason WHEN @IsWaived = 0 THEN NULL ELSE waive_reason END,
    updated_at = @Now
WHERE organization_id = @OrganizationId AND offboarding_id = @OffboardingId AND id = @Id AND status <> 'Cancelled';",
            new
            {
                OrganizationId = organizationId, OffboardingId = offboardingId, Id = taskId,
                Title = task.Title.Trim(), Description = Trim(task.Description), Category = category, AssignmentType = assignment,
                task.AssignedUserId, DueDate = task.DueDate?.Date, task.IsRequired, task.RequiresDocument,
                DocumentCategory = Trim(task.DocumentCategory), task.RequiresApproval,
                Status = status, IsWaived = isWaived, WaiveReason = Trim(waiveReason), Now = now
            }, cancellationToken: cancellationToken));
        if (affected == 0) return false;

        if (task.RequiresDocument)
        {
            var hasDoc = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.nh_hr_offboarding_documents WHERE organization_id = @OrganizationId AND offboarding_task_id = @TaskId) THEN 1 ELSE 0 END;",
                new { OrganizationId = organizationId, TaskId = taskId }, cancellationToken: cancellationToken)) == 1;
            if (!hasDoc)
                await InsertDocumentRequirementCoreAsync(connection, null, organizationId, offboardingId, taskId, task, now, cancellationToken);
        }

        // Keep the mirrored ATS task (if any) in sync; failures never block offboarding.
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE t
SET t.title = @Title, t.due_at = @DueAt, t.assigned_to_user_id = @AssignedUserId
FROM dbo.tasks t
INNER JOIN dbo.nh_hr_offboarding_tasks ot ON ot.ats_task_id = t.id
WHERE ot.organization_id = @OrganizationId AND ot.id = @TaskId;",
                new
                {
                    OrganizationId = organizationId, TaskId = taskId, Title = task.Title.Trim(),
                    DueAt = ToDueAt(task.DueDate), task.AssignedUserId
                }, cancellationToken: cancellationToken));
        }
        catch (SqlException ex)
        {
            _logger.LogDebug(ex, "Could not sync ATS task for offboarding task {TaskId}.", taskId);
        }
        return true;
    }

    public async Task<bool> CancelTaskAsync(Guid organizationId, Guid offboardingId, Guid taskId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_offboarding_tasks SET status = 'Cancelled', updated_at = @Now
WHERE organization_id = @OrganizationId AND offboarding_id = @OffboardingId AND id = @Id AND status <> 'Cancelled';",
            new { OrganizationId = organizationId, OffboardingId = offboardingId, Id = taskId, Now = DateTimeOffset.UtcNow },
            cancellationToken: cancellationToken));
        if (affected > 0) await SetAtsTaskStatusAsync(connection, organizationId, taskId, "done", cancellationToken);
        return affected > 0;
    }

    public async Task<bool> CompleteTaskAsync(Guid organizationId, Guid offboardingId, Guid taskId, string? notes, string? evidenceUrl, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_offboarding_tasks
SET status = 'Completed', completed_at = @Now, completed_by = @ActorUserId, completion_notes = @Notes,
    evidence_url = ISNULL(@EvidenceUrl, evidence_url),
    approval_status = CASE WHEN requires_approval = 1 THEN 'Pending' ELSE NULL END,
    approved_by = NULL, approved_at = NULL, updated_at = @Now
WHERE organization_id = @OrganizationId AND offboarding_id = @OffboardingId AND id = @Id
  AND status NOT IN ('Completed', 'Cancelled') AND is_waived = 0;",
            new
            {
                OrganizationId = organizationId, OffboardingId = offboardingId, Id = taskId,
                Notes = Trim(notes), EvidenceUrl = Trim(evidenceUrl), Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId
            }, cancellationToken: cancellationToken));
        if (affected > 0) await SetAtsTaskStatusAsync(connection, organizationId, taskId, "done", cancellationToken);
        return affected > 0;
    }

    public async Task<bool> ReopenTaskAsync(Guid organizationId, Guid offboardingId, Guid taskId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_offboarding_tasks
SET status = 'InProgress', completed_at = NULL, completed_by = NULL, completion_notes = NULL,
    approval_status = NULL, approved_by = NULL, approved_at = NULL, updated_at = @Now
WHERE organization_id = @OrganizationId AND offboarding_id = @OffboardingId AND id = @Id AND status = 'Completed';",
            new { OrganizationId = organizationId, OffboardingId = offboardingId, Id = taskId, Now = DateTimeOffset.UtcNow },
            cancellationToken: cancellationToken));
        if (affected > 0) await SetAtsTaskStatusAsync(connection, organizationId, taskId, "todo", cancellationToken);
        return affected > 0;
    }

    public async Task<bool> ApproveTaskAsync(Guid organizationId, Guid offboardingId, Guid taskId, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_offboarding_tasks
SET approval_status = 'Approved', approved_by = @ActorUserId, approved_at = @Now, updated_at = @Now
WHERE organization_id = @OrganizationId AND offboarding_id = @OffboardingId AND id = @Id
  AND status = 'Completed' AND requires_approval = 1 AND approval_status = 'Pending';",
            new { OrganizationId = organizationId, OffboardingId = offboardingId, Id = taskId, Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId },
            cancellationToken: cancellationToken));
        return affected > 0;
    }

    public async Task<bool> RejectTaskAsync(Guid organizationId, Guid offboardingId, Guid taskId, string reason, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_offboarding_tasks
SET approval_status = 'Rejected', approved_by = @ActorUserId, approved_at = @Now,
    status = 'InProgress', completed_at = NULL, completed_by = NULL, completion_notes = @Reason, updated_at = @Now
WHERE organization_id = @OrganizationId AND offboarding_id = @OffboardingId AND id = @Id
  AND status = 'Completed' AND requires_approval = 1 AND approval_status = 'Pending';",
            new
            {
                OrganizationId = organizationId, OffboardingId = offboardingId, Id = taskId,
                Reason = Trim(reason), Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId
            }, cancellationToken: cancellationToken));
        if (affected > 0) await SetAtsTaskStatusAsync(connection, organizationId, taskId, "todo", cancellationToken);
        return affected > 0;
    }

    public async Task<bool> WaiveTaskAsync(Guid organizationId, Guid offboardingId, Guid taskId, string reason, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_offboarding_tasks
SET is_waived = 1, waive_reason = @Reason, updated_at = @Now
WHERE organization_id = @OrganizationId AND offboarding_id = @OffboardingId AND id = @Id
  AND status NOT IN ('Completed', 'Cancelled') AND is_waived = 0;",
            new { OrganizationId = organizationId, OffboardingId = offboardingId, Id = taskId, Reason = Trim(reason), Now = DateTimeOffset.UtcNow },
            cancellationToken: cancellationToken));
        if (affected > 0) await SetAtsTaskStatusAsync(connection, organizationId, taskId, "done", cancellationToken);
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

    public async Task LinkAtsTasksAsync(Guid organizationId, Guid offboardingId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = OpenConnection();
            var rows = (await connection.QueryAsync<AtsLinkRow>(new CommandDefinition(@"
SELECT ot.id AS TaskId, ot.title AS Title, ot.assigned_user_id AS AssignedUserId, ot.due_date AS DueDate,
       e.application_id AS ApplicationId
FROM dbo.nh_hr_offboarding_tasks ot
INNER JOIN dbo.nh_hr_employee_offboardings o ON o.id = ot.offboarding_id AND o.organization_id = ot.organization_id
INNER JOIN dbo.nh_hr_employees e ON e.id = o.employee_id AND e.organization_id = o.organization_id
WHERE ot.organization_id = @OrganizationId AND ot.offboarding_id = @OffboardingId
  AND ot.assigned_user_id IS NOT NULL AND ot.ats_task_id IS NULL
  AND ot.status NOT IN ('Completed', 'Cancelled') AND ot.is_waived = 0;",
                new { OrganizationId = organizationId, OffboardingId = offboardingId }, cancellationToken: cancellationToken))).ToList();

            foreach (var row in rows)
            {
                var atsId = Guid.NewGuid();
                try
                {
                    // application_id may be NOT NULL in some deployments; the insert then fails and the link is skipped.
                    await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.tasks (id, application_id, assigned_to_user_id, title, due_at, status, created_at, source_type, source_id)
VALUES (@Id, @ApplicationId, @AssignedUserId, @Title, @DueAt, 'todo', @Now, 'offboarding', @SourceId);",
                        new
                        {
                            Id = atsId, row.ApplicationId, row.AssignedUserId, Title = row.Title,
                            DueAt = ToDueAt(row.DueDate), Now = DateTimeOffset.UtcNow, SourceId = row.TaskId
                        }, cancellationToken: cancellationToken));
                }
                catch (SqlException ex)
                {
                    _logger.LogDebug(ex, "Skipping ATS task link for offboarding task {TaskId}.", row.TaskId);
                    continue;
                }

                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE dbo.nh_hr_offboarding_tasks SET ats_task_id = @AtsId WHERE organization_id = @OrganizationId AND id = @TaskId;",
                    new { AtsId = atsId, OrganizationId = organizationId, TaskId = row.TaskId }, cancellationToken: cancellationToken));
            }
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "ATS task linking failed for offboarding {OffboardingId}.", offboardingId);
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
INNER JOIN dbo.nh_hr_offboarding_tasks ot ON ot.ats_task_id = t.id
WHERE ot.organization_id = @OrganizationId AND ot.id = @TaskId;",
                new { Status = atsStatus, Now = DateTimeOffset.UtcNow, OrganizationId = organizationId, TaskId = taskId },
                cancellationToken: cancellationToken));
        }
        catch (SqlException ex)
        {
            _logger.LogDebug(ex, "Could not update ATS task status for offboarding task {TaskId}.", taskId);
        }
    }

    private static DateTimeOffset? ToDueAt(DateTime? dueDate) =>
        dueDate.HasValue ? new DateTimeOffset(dueDate.Value.Date, TimeSpan.Zero) : null;

    // -------------------------------------------------------------------------
    // Assets
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<OffboardingAssetDto>> ListAssetsAsync(Guid organizationId, Guid offboardingId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        return (await connection.QueryAsync<OffboardingAssetDto>(new CommandDefinition(
            AssetSelect + " WHERE organization_id = @OrganizationId AND offboarding_id = @OffboardingId ORDER BY created_at, asset_name;",
            new { OrganizationId = organizationId, OffboardingId = offboardingId }, cancellationToken: cancellationToken))).ToList();
    }

    public async Task<OffboardingAssetDto?> GetAssetAsync(Guid organizationId, Guid offboardingId, Guid assetId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        return await connection.QuerySingleOrDefaultAsync<OffboardingAssetDto>(new CommandDefinition(
            AssetSelect + " WHERE organization_id = @OrganizationId AND offboarding_id = @OffboardingId AND id = @Id;",
            new { OrganizationId = organizationId, OffboardingId = offboardingId, Id = assetId }, cancellationToken: cancellationToken));
    }

    public async Task<Guid> AddAssetAsync(Guid organizationId, Guid offboardingId, OffboardingAssetSpec asset, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var id = Guid.NewGuid();
        await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_offboarding_assets
    (id, organization_id, offboarding_id, asset_name, asset_type, serial_number, return_required,
     expected_return_date, status, notes, created_at)
VALUES
    (@Id, @OrganizationId, @OffboardingId, @AssetName, @AssetType, @SerialNumber, @ReturnRequired,
     @ExpectedReturnDate, 'PendingReturn', @Notes, @Now);",
            new
            {
                Id = id, OrganizationId = organizationId, OffboardingId = offboardingId,
                AssetName = asset.AssetName.Trim(), AssetType = Trim(asset.AssetType), SerialNumber = Trim(asset.SerialNumber),
                asset.ReturnRequired, ExpectedReturnDate = asset.ExpectedReturnDate?.Date, Notes = Trim(asset.Notes), Now = DateTimeOffset.UtcNow
            }, cancellationToken: cancellationToken));
        return id;
    }

    public async Task<bool> UpdateAssetAsync(Guid organizationId, Guid offboardingId, Guid assetId, OffboardingAssetSpec asset, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_offboarding_assets
SET asset_name = @AssetName, asset_type = @AssetType, serial_number = @SerialNumber, return_required = @ReturnRequired,
    expected_return_date = @ExpectedReturnDate, notes = @Notes, updated_at = @Now
WHERE organization_id = @OrganizationId AND offboarding_id = @OffboardingId AND id = @Id;",
            new
            {
                OrganizationId = organizationId, OffboardingId = offboardingId, Id = assetId,
                AssetName = asset.AssetName.Trim(), AssetType = Trim(asset.AssetType), SerialNumber = Trim(asset.SerialNumber),
                asset.ReturnRequired, ExpectedReturnDate = asset.ExpectedReturnDate?.Date, Notes = Trim(asset.Notes), Now = DateTimeOffset.UtcNow
            }, cancellationToken: cancellationToken));
        return affected > 0;
    }

    public async Task<bool> DeleteAssetAsync(Guid organizationId, Guid offboardingId, Guid assetId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.nh_hr_offboarding_assets WHERE organization_id = @OrganizationId AND offboarding_id = @OffboardingId AND id = @Id;",
            new { OrganizationId = organizationId, OffboardingId = offboardingId, Id = assetId }, cancellationToken: cancellationToken));
        return affected > 0;
    }

    public async Task<bool> UpdateAssetStatusAsync(
        Guid organizationId, Guid offboardingId, Guid assetId, string status, UpdateOffboardingAssetStatusRequest request,
        Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_offboarding_assets
SET status = @Status,
    actual_return_date = CASE WHEN @Status = 'Returned' THEN @ReturnDate WHEN @Status = 'PendingReturn' THEN NULL ELSE actual_return_date END,
    received_by = CASE WHEN @Status = 'Returned' THEN @ActorUserId WHEN @Status = 'PendingReturn' THEN NULL ELSE received_by END,
    condition_notes = ISNULL(@ConditionNotes, condition_notes),
    evidence_url = ISNULL(@EvidenceUrl, evidence_url),
    notes = ISNULL(@Notes, notes),
    waive_reason = CASE WHEN @Status = 'Waived' THEN @WaiveReason ELSE NULL END,
    updated_at = @Now
WHERE organization_id = @OrganizationId AND offboarding_id = @OffboardingId AND id = @Id;",
            new
            {
                OrganizationId = organizationId, OffboardingId = offboardingId, Id = assetId, Status = status,
                ReturnDate = (request.ActualReturnDate ?? DateTime.UtcNow).Date, ActorUserId = actorUserId,
                ConditionNotes = Trim(request.ConditionNotes), EvidenceUrl = Trim(request.EvidenceUrl),
                Notes = Trim(request.Notes), WaiveReason = Trim(request.WaiveReason), Now = DateTimeOffset.UtcNow
            }, cancellationToken: cancellationToken));
        return affected > 0;
    }

    // -------------------------------------------------------------------------
    // Access items
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<OffboardingAccessItemDto>> ListAccessItemsAsync(Guid organizationId, Guid offboardingId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        return (await connection.QueryAsync<OffboardingAccessItemDto>(new CommandDefinition(
            AccessSelect + " WHERE x.organization_id = @OrganizationId AND x.offboarding_id = @OffboardingId ORDER BY x.created_at, x.system_name;",
            new { OrganizationId = organizationId, OffboardingId = offboardingId }, cancellationToken: cancellationToken))).ToList();
    }

    public async Task<OffboardingAccessItemDto?> GetAccessItemAsync(Guid organizationId, Guid offboardingId, Guid accessItemId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        return await connection.QuerySingleOrDefaultAsync<OffboardingAccessItemDto>(new CommandDefinition(
            AccessSelect + " WHERE x.organization_id = @OrganizationId AND x.offboarding_id = @OffboardingId AND x.id = @Id;",
            new { OrganizationId = organizationId, OffboardingId = offboardingId, Id = accessItemId }, cancellationToken: cancellationToken));
    }

    public async Task<Guid> AddAccessItemAsync(Guid organizationId, Guid offboardingId, OffboardingAccessSpec item, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var id = Guid.NewGuid();
        await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_offboarding_access_items
    (id, organization_id, offboarding_id, system_name, responsible_user_id, scheduled_at, status, notes, created_at)
VALUES
    (@Id, @OrganizationId, @OffboardingId, @SystemName, @ResponsibleUserId, @ScheduledAt,
     CASE WHEN @ScheduledAt IS NULL THEN 'Pending' ELSE 'Scheduled' END, @Notes, @Now);",
            new
            {
                Id = id, OrganizationId = organizationId, OffboardingId = offboardingId,
                SystemName = item.SystemName.Trim(), item.ResponsibleUserId, item.ScheduledAt,
                Notes = Trim(item.Notes), Now = DateTimeOffset.UtcNow
            }, cancellationToken: cancellationToken));
        return id;
    }

    public async Task<bool> UpdateAccessItemAsync(Guid organizationId, Guid offboardingId, Guid accessItemId, OffboardingAccessSpec item, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_offboarding_access_items
SET system_name = @SystemName, responsible_user_id = @ResponsibleUserId, scheduled_at = @ScheduledAt,
    notes = @Notes, updated_at = @Now
WHERE organization_id = @OrganizationId AND offboarding_id = @OffboardingId AND id = @Id;",
            new
            {
                OrganizationId = organizationId, OffboardingId = offboardingId, Id = accessItemId,
                SystemName = item.SystemName.Trim(), item.ResponsibleUserId, item.ScheduledAt,
                Notes = Trim(item.Notes), Now = DateTimeOffset.UtcNow
            }, cancellationToken: cancellationToken));
        return affected > 0;
    }

    public async Task<bool> DeleteAccessItemAsync(Guid organizationId, Guid offboardingId, Guid accessItemId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.nh_hr_offboarding_access_items WHERE organization_id = @OrganizationId AND offboarding_id = @OffboardingId AND id = @Id;",
            new { OrganizationId = organizationId, OffboardingId = offboardingId, Id = accessItemId }, cancellationToken: cancellationToken));
        return affected > 0;
    }

    public async Task<bool> UpdateAccessItemStatusAsync(
        Guid organizationId, Guid offboardingId, Guid accessItemId, string status, UpdateOffboardingAccessStatusRequest request,
        Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_offboarding_access_items
SET status = @Status,
    scheduled_at = ISNULL(@ScheduledAt, scheduled_at),
    revoked_at = CASE WHEN @Status = 'Revoked' THEN @Now
                      WHEN @Status IN ('Pending', 'Scheduled', 'NotApplicable') THEN NULL
                      ELSE revoked_at END,
    verified_by = CASE WHEN @Status = 'Verified' THEN @ActorUserId ELSE NULL END,
    verified_at = CASE WHEN @Status = 'Verified' THEN @Now ELSE NULL END,
    evidence_url = ISNULL(@EvidenceUrl, evidence_url),
    notes = ISNULL(@Notes, notes),
    updated_at = @Now
WHERE organization_id = @OrganizationId AND offboarding_id = @OffboardingId AND id = @Id;",
            new
            {
                OrganizationId = organizationId, OffboardingId = offboardingId, Id = accessItemId, Status = status,
                request.ScheduledAt, EvidenceUrl = Trim(request.EvidenceUrl), Notes = Trim(request.Notes),
                Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId
            }, cancellationToken: cancellationToken));
        return affected > 0;
    }

    // -------------------------------------------------------------------------
    // Settlement
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<OffboardingSettlementItemDto>> ListSettlementItemsAsync(Guid organizationId, Guid offboardingId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        return (await connection.QueryAsync<OffboardingSettlementItemDto>(new CommandDefinition(
            SettlementSelect + " WHERE organization_id = @OrganizationId AND offboarding_id = @OffboardingId ORDER BY created_at, title;",
            new { OrganizationId = organizationId, OffboardingId = offboardingId }, cancellationToken: cancellationToken))).ToList();
    }

    public async Task<OffboardingSettlementItemDto?> GetSettlementItemAsync(Guid organizationId, Guid offboardingId, Guid itemId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        return await connection.QuerySingleOrDefaultAsync<OffboardingSettlementItemDto>(new CommandDefinition(
            SettlementSelect + " WHERE organization_id = @OrganizationId AND offboarding_id = @OffboardingId AND id = @Id;",
            new { OrganizationId = organizationId, OffboardingId = offboardingId, Id = itemId }, cancellationToken: cancellationToken));
    }

    public async Task<bool> UpdateSettlementItemAsync(
        Guid organizationId, Guid offboardingId, Guid itemId, string status, UpdateOffboardingSettlementItemRequest request,
        Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_offboarding_settlement_items
SET status = @Status, amount = @Amount, currency = @Currency, notes = @Notes, evidence_url = @EvidenceUrl,
    completed_at = CASE WHEN @Status = 'Completed' THEN COALESCE(completed_at, @Now) ELSE NULL END,
    completed_by = CASE WHEN @Status = 'Completed' THEN COALESCE(completed_by, @ActorUserId) ELSE NULL END
WHERE organization_id = @OrganizationId AND offboarding_id = @OffboardingId AND id = @Id;",
            new
            {
                OrganizationId = organizationId, OffboardingId = offboardingId, Id = itemId, Status = status,
                request.Amount, Currency = Trim(request.Currency)?.ToUpperInvariant(),
                Notes = Trim(request.Notes), EvidenceUrl = Trim(request.EvidenceUrl),
                Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId
            }, cancellationToken: cancellationToken));
        return affected > 0;
    }

    // -------------------------------------------------------------------------
    // Exit interview
    // -------------------------------------------------------------------------

    public async Task<OffboardingExitInterviewDto?> GetExitInterviewAsync(Guid organizationId, Guid offboardingId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        return await connection.QuerySingleOrDefaultAsync<OffboardingExitInterviewDto>(new CommandDefinition(
            ExitInterviewSelect + " WHERE x.organization_id = @OrganizationId AND x.offboarding_id = @OffboardingId;",
            new { OrganizationId = organizationId, OffboardingId = offboardingId }, cancellationToken: cancellationToken));
    }

    public async Task<bool> UpdateExitInterviewAsync(
        Guid organizationId, Guid offboardingId, string status, UpdateOffboardingExitInterviewRequest request,
        bool updateConfidential, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var args = new
        {
            OrganizationId = organizationId, OffboardingId = offboardingId, Status = status,
            request.InterviewerUserId, request.ScheduledAt,
            Questionnaire = NullIfBlank(request.QuestionnaireJson),
            Responses = NullIfBlank(request.ResponsesJson),
            HrNotes = Trim(request.HrNotes),
            UpdateConfidential = updateConfidential ? 1 : 0,
            Now = DateTimeOffset.UtcNow, Id = Guid.NewGuid()
        };

        var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_offboarding_exit_interviews
SET interviewer_user_id = @InterviewerUserId, scheduled_at = @ScheduledAt, status = @Status,
    questionnaire_json = ISNULL(@Questionnaire, questionnaire_json),
    responses_json = CASE WHEN @UpdateConfidential = 1 THEN @Responses ELSE responses_json END,
    hr_notes = CASE WHEN @UpdateConfidential = 1 THEN @HrNotes ELSE hr_notes END,
    completed_at = CASE WHEN @Status = 'Completed' THEN COALESCE(completed_at, @Now) ELSE NULL END,
    updated_at = @Now
WHERE organization_id = @OrganizationId AND offboarding_id = @OffboardingId;", args, cancellationToken: cancellationToken));
        if (affected > 0) return true;

        // Offboardings created before the exit interview row existed: create it on first update.
        var exists = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.nh_hr_employee_offboardings WHERE organization_id = @OrganizationId AND id = @OffboardingId) THEN 1 ELSE 0 END;",
            new { OrganizationId = organizationId, OffboardingId = offboardingId }, cancellationToken: cancellationToken)) == 1;
        if (!exists) return false;

        await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_offboarding_exit_interviews
    (id, organization_id, offboarding_id, interviewer_user_id, scheduled_at, status, questionnaire_json,
     responses_json, hr_notes, completed_at, created_at)
VALUES
    (@Id, @OrganizationId, @OffboardingId, @InterviewerUserId, @ScheduledAt, @Status,
     COALESCE(@Questionnaire, @DefaultQuestionnaire),
     CASE WHEN @UpdateConfidential = 1 THEN @Responses ELSE NULL END,
     CASE WHEN @UpdateConfidential = 1 THEN @HrNotes ELSE NULL END,
     CASE WHEN @Status = 'Completed' THEN @Now ELSE NULL END, @Now);",
            new
            {
                args.OrganizationId, args.OffboardingId, args.Status, args.InterviewerUserId, args.ScheduledAt,
                args.Questionnaire, args.Responses, args.HrNotes, args.UpdateConfidential, args.Now, args.Id,
                DefaultQuestionnaire = OffboardingDefaults.ExitQuestionnaireJson
            }, cancellationToken: cancellationToken));
        return true;
    }

    // -------------------------------------------------------------------------
    // Documents
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<OffboardingDocumentDto>> ListDocumentsAsync(Guid organizationId, Guid offboardingId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        return (await connection.QueryAsync<OffboardingDocumentDto>(new CommandDefinition(
            DocumentSelect + " WHERE organization_id = @OrganizationId AND offboarding_id = @OffboardingId ORDER BY created_at, name;",
            new { OrganizationId = organizationId, OffboardingId = offboardingId }, cancellationToken: cancellationToken))).ToList();
    }

    public async Task<OffboardingDocumentDto?> GetDocumentAsync(Guid organizationId, Guid offboardingId, Guid documentId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        return await connection.QuerySingleOrDefaultAsync<OffboardingDocumentDto>(new CommandDefinition(
            DocumentSelect + " WHERE organization_id = @OrganizationId AND offboarding_id = @OffboardingId AND id = @Id;",
            new { OrganizationId = organizationId, OffboardingId = offboardingId, Id = documentId }, cancellationToken: cancellationToken));
    }

    public async Task<bool> UpdateDocumentAsync(Guid organizationId, Guid offboardingId, Guid documentId, string status, Guid? employeeDocumentId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.nh_hr_offboarding_documents
SET status = @Status, employee_document_id = ISNULL(@EmployeeDocumentId, employee_document_id)
WHERE organization_id = @OrganizationId AND offboarding_id = @OffboardingId AND id = @Id;",
            new { OrganizationId = organizationId, OffboardingId = offboardingId, Id = documentId, Status = status, EmployeeDocumentId = employeeDocumentId },
            cancellationToken: cancellationToken));
        return affected > 0;
    }

    // -------------------------------------------------------------------------
    // Activity
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<OffboardingActivityDto>> ListActivityAsync(Guid organizationId, Guid offboardingId, int take, CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT TOP (@Take) id AS Id, offboarding_id AS OffboardingId, activity_type AS ActivityType, summary AS Summary,
       details_json AS DetailsJson, created_at AS CreatedAt, created_by AS CreatedBy, created_by_name AS CreatedByName
FROM dbo.nh_hr_offboarding_activity
WHERE organization_id = @OrganizationId AND offboarding_id = @OffboardingId
ORDER BY created_at DESC;";

        using var connection = OpenConnection();
        return (await connection.QueryAsync<OffboardingActivityDto>(new CommandDefinition(sql,
            new { OrganizationId = organizationId, OffboardingId = offboardingId, Take = Math.Clamp(take, 1, 500) },
            cancellationToken: cancellationToken))).ToList();
    }

    public async Task AddActivityAsync(
        Guid organizationId, Guid offboardingId, string activityType, string summary, string? detailsJson,
        Guid? actorUserId, string? actorName, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        await InsertActivityCoreAsync(connection, null, organizationId, offboardingId, activityType, summary, detailsJson, actorUserId, actorName, cancellationToken);
    }

    private static Task InsertActivityCoreAsync(
        IDbConnection connection, IDbTransaction? tx, Guid organizationId, Guid offboardingId,
        string activityType, string summary, string? detailsJson, Guid? actorUserId, string? actorName, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_offboarding_activity
    (id, organization_id, offboarding_id, activity_type, summary, details_json, created_at, created_by, created_by_name)
VALUES
    (@Id, @OrganizationId, @OffboardingId, @ActivityType, @Summary, @DetailsJson, @Now, @ActorUserId, @ActorName);",
            new
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, OffboardingId = offboardingId, ActivityType = activityType,
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

    public async Task<OffboardingSummaryDto> GetSummaryAsync(Guid organizationId, DateTime? from, DateTime? to, CancellationToken cancellationToken = default)
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

        var summary = new OffboardingSummaryDto { PeriodStart = periodStart, PeriodEnd = periodEnd };

        var byStatus = await connection.QueryAsync<KeyCount>(new CommandDefinition(@"
SELECT o.status AS [Key], COUNT(1) AS [Count]
FROM dbo.nh_hr_employee_offboardings o
WHERE o.organization_id = @OrganizationId
GROUP BY o.status;", org, cancellationToken: cancellationToken));
        summary.ByStatus = OffboardingStatuses.All.ToDictionary(s => s, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var row in byStatus) summary.ByStatus[row.Key] = row.Count;
        summary.Scheduled = summary.ByStatus.GetValueOrDefault(OffboardingStatuses.Scheduled);
        summary.InProgress = summary.ByStatus.GetValueOrDefault(OffboardingStatuses.InProgress);
        summary.PendingClearance = summary.ByStatus.GetValueOrDefault(OffboardingStatuses.PendingClearance);
        summary.ActiveOffboardings = summary.Scheduled + summary.InProgress + summary.PendingClearance;

        var byDept = await connection.QueryAsync<KeyCount>(new CommandDefinition(@"
SELECT COALESCE(d.name, 'Unassigned') AS [Key], COUNT(1) AS [Count]
FROM dbo.nh_hr_employee_offboardings o
INNER JOIN dbo.nh_hr_employees e ON e.id = o.employee_id AND e.organization_id = o.organization_id
LEFT JOIN dbo.nh_hr_departments d ON d.id = e.department_id AND d.organization_id = e.organization_id
WHERE o.organization_id = @OrganizationId AND o.status IN ('Scheduled', 'InProgress', 'PendingClearance')
GROUP BY COALESCE(d.name, 'Unassigned');", org, cancellationToken: cancellationToken));
        summary.ByDepartment = byDept.ToDictionary(r => r.Key, r => r.Count);

        var bySeparation = await connection.QueryAsync<KeyCount>(new CommandDefinition(@"
SELECT o.separation_type AS [Key], COUNT(1) AS [Count]
FROM dbo.nh_hr_employee_offboardings o
WHERE o.organization_id = @OrganizationId AND o.status <> 'Cancelled'
  AND (o.status IN ('Scheduled', 'InProgress', 'PendingClearance')
       OR (o.status = 'Completed' AND o.actual_completion_date BETWEEN @Start AND @End))
GROUP BY o.separation_type;", new { OrganizationId = organizationId, Start = periodStart, End = periodEnd }, cancellationToken: cancellationToken));
        summary.BySeparationType = SeparationTypes.All.ToDictionary(s => s, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var row in bySeparation) summary.BySeparationType[row.Key] = row.Count;

        summary.StartedInPeriod = await CountStartedAsync(connection, organizationId, periodStart, periodEnd, cancellationToken);
        summary.PreviousStartedInPeriod = await CountStartedAsync(connection, organizationId, prevStart, prevEnd, cancellationToken);
        summary.CompletedInPeriod = await CountCompletedAsync(connection, organizationId, periodStart, periodEnd, cancellationToken);
        summary.PreviousCompletedInPeriod = await CountCompletedAsync(connection, organizationId, prevStart, prevEnd, cancellationToken);
        summary.AverageCompletionDays = await AverageCompletionDaysAsync(connection, organizationId, periodStart, periodEnd, cancellationToken);
        summary.PreviousAverageCompletionDays = await AverageCompletionDaysAsync(connection, organizationId, prevStart, prevEnd, cancellationToken);

        summary.UpcomingDepartures = await connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(1) FROM dbo.nh_hr_employee_offboardings
WHERE organization_id = @OrganizationId AND status IN ('Scheduled', 'InProgress', 'PendingClearance')
  AND last_working_day BETWEEN @Today AND @Horizon;",
            new { OrganizationId = organizationId, Today = today, Horizon = today.AddDays(7) }, cancellationToken: cancellationToken));

        summary.PendingAssetReturns = await connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(1)
FROM dbo.nh_hr_offboarding_assets a
INNER JOIN dbo.nh_hr_employee_offboardings o ON o.id = a.offboarding_id AND o.organization_id = a.organization_id
WHERE a.organization_id = @OrganizationId AND o.status IN ('Scheduled', 'InProgress', 'PendingClearance')
  AND a.return_required = 1 AND a.status NOT IN ('Returned', 'Waived');", org, cancellationToken: cancellationToken));

        summary.PendingAccessRevocations = await connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(1)
FROM dbo.nh_hr_offboarding_access_items x
INNER JOIN dbo.nh_hr_employee_offboardings o ON o.id = x.offboarding_id AND o.organization_id = x.organization_id
WHERE x.organization_id = @OrganizationId AND o.status IN ('Scheduled', 'InProgress', 'PendingClearance')
  AND x.status NOT IN ('Revoked', 'Verified', 'NotApplicable');", org, cancellationToken: cancellationToken));

        // Progress / overdue figures reuse the pure rules so they always agree with list and detail views.
        var states = (await connection.QueryAsync<OffboardingTaskStateRow>(new CommandDefinition(
            TaskStateSelect + @"
WHERE organization_id = @OrganizationId
  AND offboarding_id IN (SELECT id FROM dbo.nh_hr_employee_offboardings
                         WHERE organization_id = @OrganizationId AND status IN ('Scheduled', 'InProgress', 'PendingClearance'));",
            org, cancellationToken: cancellationToken))).ToList();
        var grouped = states.GroupBy(s => s.OffboardingId).ToList();
        summary.OverdueTasks = grouped.Sum(g => OffboardingValidation.CountOverdue(g, today));
        summary.OverdueOffboardings = grouped.Count(g => OffboardingValidation.CountOverdue(g, today) > 0);
        summary.AverageProgressPercent = grouped.Count == 0
            ? 0
            : Math.Round(grouped.Average(g => OffboardingValidation.ComputeProgressPercent(g)), 1);

        return summary;
    }

    private static Task<int> CountStartedAsync(IDbConnection connection, Guid organizationId, DateTime start, DateTime end, CancellationToken ct) =>
        connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(1) FROM dbo.nh_hr_employee_offboardings
WHERE organization_id = @OrganizationId AND status <> 'Cancelled' AND process_start_date BETWEEN @Start AND @End;",
            new { OrganizationId = organizationId, Start = start, End = end }, cancellationToken: ct));

    private static Task<int> CountCompletedAsync(IDbConnection connection, Guid organizationId, DateTime start, DateTime end, CancellationToken ct) =>
        connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(1) FROM dbo.nh_hr_employee_offboardings
WHERE organization_id = @OrganizationId AND status = 'Completed' AND actual_completion_date BETWEEN @Start AND @End;",
            new { OrganizationId = organizationId, Start = start, End = end }, cancellationToken: ct));

    private static async Task<double> AverageCompletionDaysAsync(IDbConnection connection, Guid organizationId, DateTime start, DateTime end, CancellationToken ct)
    {
        var avg = await connection.ExecuteScalarAsync<double?>(new CommandDefinition(@"
SELECT AVG(CAST(DATEDIFF(day, process_start_date, actual_completion_date) AS float))
FROM dbo.nh_hr_employee_offboardings
WHERE organization_id = @OrganizationId AND status = 'Completed'
  AND process_start_date IS NOT NULL AND actual_completion_date BETWEEN @Start AND @End;",
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
FROM dbo.nh_hr_offboarding_notification_settings";

    public async Task<IReadOnlyList<OffboardingNotificationSettingDto>> GetNotificationSettingsAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var rows = await connection.QueryAsync<NotificationSettingRow>(new CommandDefinition(
            NotificationSettingSelect + " WHERE organization_id = @OrganizationId;",
            new { OrganizationId = organizationId }, cancellationToken: cancellationToken));

        var order = OffboardingTriggers.All.ToList();
        return rows.Select(ToSettingDto)
            .OrderBy(s => { var i = order.IndexOf(s.TriggerCode); return i < 0 ? int.MaxValue : i; })
            .ToList();
    }

    public async Task<OffboardingNotificationSettingDto?> GetNotificationSettingAsync(Guid organizationId, string triggerCode, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var row = await connection.QuerySingleOrDefaultAsync<NotificationSettingRow>(new CommandDefinition(
            NotificationSettingSelect + " WHERE organization_id = @OrganizationId AND trigger_code = @TriggerCode;",
            new { OrganizationId = organizationId, TriggerCode = triggerCode }, cancellationToken: cancellationToken));
        return row == null ? null : ToSettingDto(row);
    }

    public async Task UpsertNotificationSettingAsync(Guid organizationId, UpdateOffboardingNotificationSettingRequest setting, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        var recipients = setting.Recipients == null ? null : JsonSerializer.Serialize(setting.Recipients);
        var args = new
        {
            OrganizationId = organizationId, setting.TriggerCode, setting.IsEnabled, setting.ChannelEmail, setting.ChannelWhatsapp,
            setting.DaysBefore, RecipientsJson = recipients, Now = DateTimeOffset.UtcNow, ActorUserId = actorUserId, Id = Guid.NewGuid()
        };

        const string updateSql = @"
UPDATE dbo.nh_hr_offboarding_notification_settings
SET is_enabled = @IsEnabled, channel_email = @ChannelEmail, channel_whatsapp = @ChannelWhatsapp,
    days_before = @DaysBefore, recipients_json = ISNULL(@RecipientsJson, recipients_json),
    updated_at = @Now, updated_by = @ActorUserId
WHERE organization_id = @OrganizationId AND trigger_code = @TriggerCode;";

        using var connection = OpenConnection();
        var updated = await connection.ExecuteAsync(new CommandDefinition(updateSql, args, cancellationToken: cancellationToken));
        if (updated > 0) return;

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.nh_hr_offboarding_notification_settings
    (id, organization_id, trigger_code, is_enabled, channel_email, channel_whatsapp, days_before, recipients_json, updated_at, updated_by)
VALUES
    (@Id, @OrganizationId, @TriggerCode, @IsEnabled, @ChannelEmail, @ChannelWhatsapp, @DaysBefore, @RecipientsJson, @Now, @ActorUserId);",
                args, cancellationToken: cancellationToken));
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // A concurrent request created the row first; apply this update on top of it.
            await connection.ExecuteAsync(new CommandDefinition(updateSql, args, cancellationToken: cancellationToken));
        }
    }

    private static OffboardingNotificationSettingDto ToSettingDto(NotificationSettingRow row)
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

        return new OffboardingNotificationSettingDto
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

    public async Task<IReadOnlyList<OffboardingNotificationDto>> ListNotificationsAsync(Guid organizationId, Guid? offboardingId, int take, CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT TOP (@Take) id AS Id, offboarding_id AS OffboardingId, trigger_code AS TriggerCode, channel AS Channel,
       recipient AS Recipient, status AS Status, error_message AS ErrorMessage, created_at AS CreatedAt
FROM dbo.nh_hr_offboarding_notifications
WHERE organization_id = @OrganizationId AND (@OffboardingId IS NULL OR offboarding_id = @OffboardingId)
ORDER BY created_at DESC;";

        using var connection = OpenConnection();
        return (await connection.QueryAsync<OffboardingNotificationDto>(new CommandDefinition(sql,
            new { OrganizationId = organizationId, OffboardingId = offboardingId, Take = Math.Clamp(take, 1, 500) },
            cancellationToken: cancellationToken))).ToList();
    }

    public async Task<bool> RecordNotificationAttemptAsync(
        Guid organizationId, Guid? offboardingId, string triggerCode, string dedupeKey, string channel,
        string? recipient, string status, string? errorMessage, CancellationToken cancellationToken = default)
    {
        var key = dedupeKey.Length > 200 ? dedupeKey[..200] : dedupeKey;
        using var connection = OpenConnection();
        try
        {
            var inserted = await connection.ExecuteAsync(new CommandDefinition(@"
IF NOT EXISTS (SELECT 1 FROM dbo.nh_hr_offboarding_notifications WHERE organization_id = @OrganizationId AND dedupe_key = @DedupeKey)
BEGIN
    INSERT INTO dbo.nh_hr_offboarding_notifications
        (id, organization_id, offboarding_id, trigger_code, dedupe_key, channel, recipient, status, error_message, created_at)
    VALUES
        (@Id, @OrganizationId, @OffboardingId, @TriggerCode, @DedupeKey, @Channel, @Recipient, @Status, @ErrorMessage, @Now);
END;",
                new
                {
                    Id = Guid.NewGuid(), OrganizationId = organizationId, OffboardingId = offboardingId, TriggerCode = triggerCode,
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
UPDATE dbo.nh_hr_offboarding_notifications SET status = @Status, error_message = @ErrorMessage
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

    public async Task<IReadOnlyList<OffboardingNotificationRecipient>> ResolveNotificationRecipientsAsync(
        Guid organizationId, Guid offboardingId, Guid? taskId, IReadOnlyCollection<string> roles, CancellationToken cancellationToken = default)
    {
        var result = new List<OffboardingNotificationRecipient>();
        using var connection = OpenConnection();
        var args = new
        {
            OrganizationId = organizationId, OffboardingId = offboardingId, TaskId = taskId,
            Involuntary = SeparationTypes.Involuntary.ToArray()
        };

        foreach (var role in roles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string? sql = role.ToLowerInvariant() switch
            {
                "owner" => @"
SELECT u.email AS Email, COALESCE(u.display_name, u.email) AS Name
FROM dbo.nh_hr_employee_offboardings o
INNER JOIN dbo.nh_users u ON u.id = o.owner_user_id AND u.org_id = o.organization_id
WHERE o.organization_id = @OrganizationId AND o.id = @OffboardingId;",
                "assignee" when taskId.HasValue => @"
SELECT u.email AS Email, COALESCE(u.display_name, u.email) AS Name
FROM dbo.nh_hr_offboarding_tasks t
INNER JOIN dbo.nh_users u ON u.id = t.assigned_user_id AND u.org_id = t.organization_id
WHERE t.organization_id = @OrganizationId AND t.id = @TaskId;",
                // The employee is only ever notified on explicit opt-in, and never about involuntary separations.
                "employee" => @"
SELECT e.work_email AS Email, " + EmployeeNameSql + @" AS Name
FROM dbo.nh_hr_employee_offboardings o
INNER JOIN dbo.nh_hr_employees e ON e.id = o.employee_id AND e.organization_id = o.organization_id
WHERE o.organization_id = @OrganizationId AND o.id = @OffboardingId
  AND o.notify_employee = 1 AND o.separation_type NOT IN @Involuntary;",
                "directmanager" => @"
SELECT e.work_email AS Email, " + EmployeeNameSql + @" AS Name
FROM dbo.nh_hr_employee_offboardings o
INNER JOIN dbo.nh_hr_employees emp ON emp.id = o.employee_id AND emp.organization_id = o.organization_id
INNER JOIN dbo.nh_hr_employees e ON e.id = emp.manager_id AND e.organization_id = emp.organization_id
WHERE o.organization_id = @OrganizationId AND o.id = @OffboardingId;",
                _ => null
            };
            if (sql == null) continue;

            var row = await connection.QueryFirstOrDefaultAsync<RecipientRow>(new CommandDefinition(sql, args, cancellationToken: cancellationToken));
            if (row != null) result.Add(new OffboardingNotificationRecipient(role, Trim(row.Email), Trim(row.Name)));
        }
        return result;
    }

    // -------------------------------------------------------------------------
    // Defaults (templates + notification settings)
    // -------------------------------------------------------------------------

    private sealed record SeedTask(
        string Title, string Category, string Assignment, int? Offset,
        bool Required = true, string? DocCategory = null, bool Approval = false, string? Description = null);

    // Due offsets are days relative to the last working day (negative = before).
    private static readonly SeedTask[] VoluntaryResignationTasks =
    [
        new("Collect signed resignation letter", OffboardingTaskCategories.Documentation, OffboardingAssignmentTypes.HR, -14, true, "ResignationLetter", true,
            "Acknowledge the resignation in writing and confirm the last working day."),
        new("Manager acknowledgement and transition plan", OffboardingTaskCategories.ManagementApproval, OffboardingAssignmentTypes.DirectManager, -12, true, null, true),
        new("Agree knowledge transfer and handover plan", OffboardingTaskCategories.KnowledgeTransfer, OffboardingAssignmentTypes.DirectManager, -10),
        new("Complete knowledge transfer sessions", OffboardingTaskCategories.KnowledgeTransfer, OffboardingAssignmentTypes.Employee, -3),
        new("Reassign open work, clients and responsibilities", OffboardingTaskCategories.KnowledgeTransfer, OffboardingAssignmentTypes.DirectManager, -2),
        new("Remind employee of confidentiality obligations", OffboardingTaskCategories.Compliance, OffboardingAssignmentTypes.HR, -1, true, "ConfidentialityReminder"),
        new("Conduct exit interview", OffboardingTaskCategories.ExitInterview, OffboardingAssignmentTypes.HR, -2, false),
        new("Return company equipment and badges", OffboardingTaskCategories.EquipmentAssets, OffboardingAssignmentTypes.Employee, 0),
        new("Revoke system access and accounts", OffboardingTaskCategories.ITSecurity, OffboardingAssignmentTypes.IT, 0, true, null, true),
        new("Set up email auto-reply and forwarding", OffboardingTaskCategories.ITSecurity, OffboardingAssignmentTypes.IT, 0, false),
        new("Calculate final pay and accrued leave", OffboardingTaskCategories.FinancePayroll, OffboardingAssignmentTypes.Finance, 0, true, null, true),
        new("Process expense reimbursements and close corporate cards", OffboardingTaskCategories.FinancePayroll, OffboardingAssignmentTypes.Finance, 0),
        new("Terminate benefits and notify providers", OffboardingTaskCategories.HRAdministration, OffboardingAssignmentTypes.HR, 0),
        new("Issue employment verification letter", OffboardingTaskCategories.Documentation, OffboardingAssignmentTypes.HR, 1, false, "EmploymentLetter"),
        new("Update org chart and distribution lists", OffboardingTaskCategories.HRAdministration, OffboardingAssignmentTypes.HR, 1, false)
    ];

    private static readonly SeedTask[] InvoluntaryTerminationTasks =
    [
        new("Legal and HR review of the termination decision", OffboardingTaskCategories.Compliance, OffboardingAssignmentTypes.HR, -3, true, null, true),
        new("Management approval for termination", OffboardingTaskCategories.ManagementApproval, OffboardingAssignmentTypes.DirectManager, -2, true, null, true),
        new("Prepare termination letter and separation terms", OffboardingTaskCategories.Documentation, OffboardingAssignmentTypes.HR, -2, true, "TerminationLetter", true),
        new("Coordinate access revocation timing with IT", OffboardingTaskCategories.ITSecurity, OffboardingAssignmentTypes.IT, -1),
        new("Revoke all system access at notification", OffboardingTaskCategories.ITSecurity, OffboardingAssignmentTypes.IT, 0, true, null, true),
        new("Secure company data, mailbox and devices", OffboardingTaskCategories.ITSecurity, OffboardingAssignmentTypes.IT, 0),
        new("Collect company assets and badges", OffboardingTaskCategories.EquipmentAssets, OffboardingAssignmentTypes.HR, 0),
        new("Calculate final pay, accrued leave and severance", OffboardingTaskCategories.FinancePayroll, OffboardingAssignmentTypes.Finance, 0, true, null, true),
        new("Terminate benefits and provide continuation information", OffboardingTaskCategories.HRAdministration, OffboardingAssignmentTypes.HR, 1),
        new("Document the termination in the employee file", OffboardingTaskCategories.Documentation, OffboardingAssignmentTypes.HR, 1),
        new("Notify affected teams and reassign responsibilities", OffboardingTaskCategories.KnowledgeTransfer, OffboardingAssignmentTypes.DirectManager, 1, false),
        new("Offer exit interview", OffboardingTaskCategories.ExitInterview, OffboardingAssignmentTypes.HR, 3, false)
    ];

    private static readonly SeedTask[] ContractExpirationTasks =
    [
        new("Confirm contract end date and renewal decision", OffboardingTaskCategories.HRAdministration, OffboardingAssignmentTypes.HR, -21, true, null, true),
        new("Notify the contractor of non-renewal", OffboardingTaskCategories.Documentation, OffboardingAssignmentTypes.HR, -14, false, "NonRenewalNotice"),
        new("Hand over deliverables and documentation", OffboardingTaskCategories.KnowledgeTransfer, OffboardingAssignmentTypes.Employee, -5),
        new("Manager sign-off on deliverables", OffboardingTaskCategories.ManagementApproval, OffboardingAssignmentTypes.DirectManager, -2, true, null, true),
        new("Collect exit feedback", OffboardingTaskCategories.ExitInterview, OffboardingAssignmentTypes.HR, -1, false),
        new("Return company equipment", OffboardingTaskCategories.EquipmentAssets, OffboardingAssignmentTypes.Employee, 0),
        new("Revoke system access", OffboardingTaskCategories.ITSecurity, OffboardingAssignmentTypes.IT, 0, true, null, true),
        new("Process final payment and close contract", OffboardingTaskCategories.FinancePayroll, OffboardingAssignmentTypes.Finance, 3),
        new("Issue contract completion certificate", OffboardingTaskCategories.Documentation, OffboardingAssignmentTypes.HR, 1, false, "CompletionCertificate"),
        new("Update records and record rehire eligibility", OffboardingTaskCategories.HRAdministration, OffboardingAssignmentTypes.HR, 2)
    ];

    private static readonly SeedTask[] ItEmployeeTasks =
    [
        new("Collect signed resignation or separation notice", OffboardingTaskCategories.Documentation, OffboardingAssignmentTypes.HR, -14, true, "SeparationNotice", true),
        new("Agree knowledge transfer plan", OffboardingTaskCategories.KnowledgeTransfer, OffboardingAssignmentTypes.DirectManager, -12),
        new("Document systems, runbooks and on-call handover", OffboardingTaskCategories.KnowledgeTransfer, OffboardingAssignmentTypes.Employee, -7),
        new("Transfer repository ownership and open pull requests", OffboardingTaskCategories.KnowledgeTransfer, OffboardingAssignmentTypes.Employee, -3),
        new("Conduct exit interview", OffboardingTaskCategories.ExitInterview, OffboardingAssignmentTypes.HR, -2, false),
        new("Remind employee of IP and confidentiality obligations", OffboardingTaskCategories.Compliance, OffboardingAssignmentTypes.HR, -1, true, "NDA"),
        new("Rotate shared credentials, API keys and secrets", OffboardingTaskCategories.ITSecurity, OffboardingAssignmentTypes.IT, 0, true, null, true),
        new("Revoke SSO, MFA and email", OffboardingTaskCategories.ITSecurity, OffboardingAssignmentTypes.IT, 0, true, null, true),
        new("Remove from GitHub, CI/CD and cloud consoles", OffboardingTaskCategories.ITSecurity, OffboardingAssignmentTypes.IT, 0),
        new("Revoke VPN and production database access", OffboardingTaskCategories.ITSecurity, OffboardingAssignmentTypes.IT, 0),
        new("Collect laptop and hardware tokens", OffboardingTaskCategories.EquipmentAssets, OffboardingAssignmentTypes.Employee, 0),
        new("Wipe and re-image returned devices", OffboardingTaskCategories.EquipmentAssets, OffboardingAssignmentTypes.IT, 2),
        new("Calculate final pay and equipment deductions", OffboardingTaskCategories.FinancePayroll, OffboardingAssignmentTypes.Finance, 0, true, null, true),
        new("Terminate benefits and notify providers", OffboardingTaskCategories.HRAdministration, OffboardingAssignmentTypes.HR, 1)
    ];

    private static readonly (string Trigger, bool Enabled, bool Email, bool WhatsApp, int? DaysBefore, string[] Recipients)[] DefaultNotificationSettings =
    [
        // The Employee role is intentionally absent from every default: employee notification is an explicit opt-in.
        (OffboardingTriggers.OffboardingCreated, true, true, false, null, [OffboardingRecipientRoles.Owner]),
        (OffboardingTriggers.OffboardingStarted, true, true, false, null, [OffboardingRecipientRoles.Owner, OffboardingRecipientRoles.DirectManager]),
        (OffboardingTriggers.OffboardingPendingClearance, true, true, false, null, [OffboardingRecipientRoles.Owner]),
        (OffboardingTriggers.OffboardingCompleted, true, true, false, null, [OffboardingRecipientRoles.Owner, OffboardingRecipientRoles.DirectManager]),
        (OffboardingTriggers.OffboardingCancelled, true, true, false, null, [OffboardingRecipientRoles.Owner]),
        (OffboardingTriggers.TaskAssigned, true, true, false, null, [OffboardingRecipientRoles.Assignee]),
        (OffboardingTriggers.TaskDueSoon, true, true, false, 2, [OffboardingRecipientRoles.Assignee]),
        (OffboardingTriggers.TaskOverdue, true, true, false, null, [OffboardingRecipientRoles.Assignee, OffboardingRecipientRoles.Owner]),
        (OffboardingTriggers.TaskApprovalRequested, true, true, false, null, [OffboardingRecipientRoles.Owner]),
        (OffboardingTriggers.TaskApproved, false, true, false, null, [OffboardingRecipientRoles.Assignee]),
        (OffboardingTriggers.TaskRejected, true, true, false, null, [OffboardingRecipientRoles.Assignee]),
        (OffboardingTriggers.LastWorkingDayApproaching, true, true, false, 3, [OffboardingRecipientRoles.Owner, OffboardingRecipientRoles.DirectManager]),
        (OffboardingTriggers.AssetReturnOverdue, true, true, false, null, [OffboardingRecipientRoles.Owner])
    ];

    private async Task EnsureOrgDefaultsCoreAsync(IDbConnection connection, IDbTransaction tx, Guid organizationId, CancellationToken cancellationToken)
    {
        // Serialize concurrent first requests for the same org so defaults are never seeded twice.
        await connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
DECLARE @r int;
EXEC @r = sp_getapplock @Resource = @Resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000;
SELECT @r;",
            new { Resource = $"nh-offboarding-defaults:{organizationId:N}" }, tx, cancellationToken: cancellationToken));

        var templateCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM dbo.nh_hr_offboarding_templates WHERE organization_id = @OrganizationId;",
            new { OrganizationId = organizationId }, tx, cancellationToken: cancellationToken));

        if (templateCount == 0)
        {
            async Task<Guid?> DepartmentId(string name) => await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT TOP 1 id FROM dbo.nh_hr_departments WHERE organization_id = @OrganizationId AND name = @Name;",
                new { OrganizationId = organizationId, Name = name }, tx, cancellationToken: cancellationToken));

            await SeedTemplateAsync(connection, tx, organizationId, "Voluntary Resignation",
                "Standard offboarding when an employee resigns: notice, handover, equipment, access and final pay.",
                SeparationTypes.VoluntaryResignation, null, 14, isDefault: true, VoluntaryResignationTasks, cancellationToken);
            await SeedTemplateAsync(connection, tx, organizationId, "Involuntary Termination",
                "Employer-initiated termination: approvals, immediate access revocation, asset recovery and final settlement. Employees are not notified automatically.",
                SeparationTypes.InvoluntaryTermination, null, 7, false, InvoluntaryTerminationTasks, cancellationToken);
            await SeedTemplateAsync(connection, tx, organizationId, "Contract Expiration",
                "Fixed-term contract or contractor engagement ending: deliverable handover, sign-off and closing payment.",
                SeparationTypes.ContractExpiration, null, 21, false, ContractExpirationTasks, cancellationToken);
            await SeedTemplateAsync(connection, tx, organizationId, "IT Employee Offboarding",
                "Offboarding for engineering and IT roles: credential rotation, repository ownership, device wipe and access revocation.",
                null, await DepartmentId("IT"), 21, false, ItEmployeeTasks, cancellationToken);
        }

        foreach (var s in DefaultNotificationSettings)
        {
            await connection.ExecuteAsync(new CommandDefinition(@"
IF NOT EXISTS (SELECT 1 FROM dbo.nh_hr_offboarding_notification_settings WHERE organization_id = @OrganizationId AND trigger_code = @TriggerCode)
BEGIN
    INSERT INTO dbo.nh_hr_offboarding_notification_settings
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
        string? separationType, Guid? departmentId, int durationDays, bool isDefault, SeedTask[] tasks, CancellationToken cancellationToken)
    {
        var request = new UpsertOffboardingTemplateRequest
        {
            Name = name,
            Description = description,
            SeparationType = separationType,
            DepartmentId = departmentId,
            EstimatedDurationDays = durationDays,
            IsActive = true,
            IsDefault = isDefault,
            Tasks = tasks.Select(t => new UpsertOffboardingTemplateTaskRequest
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

    /// <summary>Loads task states for the given offboardings and fills progress / overdue figures.</summary>
    private static async Task AttachProgressAsync(
        IDbConnection connection, Guid organizationId, IReadOnlyList<OffboardingListItemDto> items, DateTime today, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return;

        var ids = items.Select(i => i.Id).ToArray();
        var states = (await connection.QueryAsync<OffboardingTaskStateRow>(new CommandDefinition(
            TaskStateSelect + " WHERE organization_id = @OrganizationId AND offboarding_id IN @Ids;",
            new { OrganizationId = organizationId, Ids = ids }, cancellationToken: cancellationToken)))
            .GroupBy(s => s.OffboardingId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var item in items)
        {
            states.TryGetValue(item.Id, out var tasks);
            ApplyProgress(item, (IReadOnlyList<IOffboardingTaskState>?)tasks ?? Array.Empty<IOffboardingTaskState>(), today);
        }
    }

    private static void ApplyProgress(OffboardingListItemDto item, IEnumerable<IOffboardingTaskState> tasks, DateTime? today = null)
    {
        var list = tasks.ToList();
        var (completed, total) = OffboardingValidation.CountProgress(list);
        item.CompletedTasks = completed;
        item.TotalTasks = total;
        item.ProgressPercent = OffboardingValidation.ComputeProgressPercent(list);
        item.OverdueCount = OffboardingValidation.IsActiveStatus(item.Status)
            ? OffboardingValidation.CountOverdue(list, today ?? DateTime.UtcNow.Date)
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
            $"Core HR offboarding schema script not found. Expected sql/{SchemaScriptFileName}.");
    }

    private static string BuildOrderBy(string? sort, string? dir)
    {
        var direction = string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
        var template = !string.IsNullOrWhiteSpace(sort) && SortColumns.TryGetValue(sort.Trim(), out var t) ? t : SortColumns["lastWorkingDay"];
        return template.Replace("{dir}", direction);
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
