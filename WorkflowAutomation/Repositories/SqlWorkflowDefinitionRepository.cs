using System.Data;
using Dapper;
using nexthire_api.Data;
using nexthire_api.WorkflowAutomation.DTOs;
using nexthire_api.WorkflowAutomation.Mapping;
using nexthire_api.WorkflowAutomation.Models;
using Nexa.WorkflowAutomation.Contracts.Persistence;
using Nexa.WorkflowAutomation.Models.Definitions;

namespace nexthire_api.WorkflowAutomation.Repositories;

public sealed class SqlWorkflowDefinitionRepository : IWorkflowDefinitionRepository, IWorkflowTriggerDefinitionLookup
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IWorkflowSqlSession _sql;

    public SqlWorkflowDefinitionRepository(
        IDbConnectionFactory connectionFactory,
        IWorkflowSqlSession sql)
    {
        _connectionFactory = connectionFactory;
        _sql = sql;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        var path = ResolveSchemaScriptPath();
        var sql = await File.ReadAllTextAsync(path, cancellationToken);
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<WorkflowDefinitionListItemResponse>> ListAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT
    id              AS Id,
    name            AS Name,
    description     AS Description,
    version         AS Version,
    status          AS Status,
    created_at      AS CreatedAt,
    updated_at      AS UpdatedAt,
    published_at    AS PublishedAt,
    archived_at     AS ArchivedAt
FROM dbo.nh_wf_definitions
WHERE organization_id = @OrganizationId
  AND is_deleted = 0
ORDER BY created_at DESC;";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        var rows = await connection.QueryAsync<WorkflowDefinitionRecord>(
            scope.CreateCommand(sql, new { OrganizationId = organizationId }, cancellationToken));

        return rows.Select(WorkflowDefinitionMapper.ToListItem).ToList();
    }

    public async Task<WorkflowDefinition?> GetByIdAsync(
        Guid organizationId,
        Guid id,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT
    id                  AS Id,
    organization_id     AS OrganizationId,
    name                AS Name,
    description         AS Description,
    version             AS Version,
    status              AS Status,
    start_step_id       AS StartStepId,
    definition          AS Definition,
    metadata            AS Metadata,
    created_at          AS CreatedAt,
    created_by          AS CreatedBy,
    updated_at          AS UpdatedAt,
    updated_by          AS UpdatedBy,
    published_at        AS PublishedAt,
    archived_at         AS ArchivedAt,
    is_deleted          AS IsDeleted,
    deleted_at          AS DeletedAt
FROM dbo.nh_wf_definitions
WHERE organization_id = @OrganizationId
  AND id = @Id
  AND is_deleted = 0;";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        var row = await connection.QuerySingleOrDefaultAsync<WorkflowDefinitionRecord>(
            scope.CreateCommand(
                sql,
                new { OrganizationId = organizationId, Id = id },
                cancellationToken));

        return row is null ? null : WorkflowDefinitionMapper.FromRecord(row);
    }

    /// <summary>
    /// Published, non-deleted definitions whose JSON trigger.type matches <paramref name="triggerType"/>.
    /// Filters in SQL via <c>JSON_VALUE</c> — does not load all definitions into memory.
    /// </summary>
    public async Task<IReadOnlyList<WorkflowDefinition>> FindPublishedByTriggerTypeAsync(
        Guid organizationId,
        string triggerType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerType);

        const string sql = @"
SELECT
    id                  AS Id,
    organization_id     AS OrganizationId,
    name                AS Name,
    description         AS Description,
    version             AS Version,
    status              AS Status,
    start_step_id       AS StartStepId,
    definition          AS Definition,
    metadata            AS Metadata,
    created_at          AS CreatedAt,
    created_by          AS CreatedBy,
    updated_at          AS UpdatedAt,
    updated_by          AS UpdatedBy,
    published_at        AS PublishedAt,
    archived_at         AS ArchivedAt,
    is_deleted          AS IsDeleted,
    deleted_at          AS DeletedAt
FROM dbo.nh_wf_definitions
WHERE organization_id = @OrganizationId
  AND status = 'Published'
  AND is_deleted = 0
  AND LOWER(JSON_VALUE(definition, '$.trigger.type')) = LOWER(@TriggerType)
ORDER BY published_at ASC, created_at ASC;";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        var rows = await connection.QueryAsync<WorkflowDefinitionRecord>(
            scope.CreateCommand(
                sql,
                new { OrganizationId = organizationId, TriggerType = triggerType.Trim() },
                cancellationToken));

        return rows.Select(WorkflowDefinitionMapper.FromRecord).ToList();
    }

    public async Task<WorkflowDefinitionRecord?> GetRecordByIdAsync(
        Guid organizationId,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT
    id                  AS Id,
    organization_id     AS OrganizationId,
    name                AS Name,
    description         AS Description,
    version             AS Version,
    status              AS Status,
    start_step_id       AS StartStepId,
    definition          AS Definition,
    metadata            AS Metadata,
    created_at          AS CreatedAt,
    created_by          AS CreatedBy,
    updated_at          AS UpdatedAt,
    updated_by          AS UpdatedBy,
    published_at        AS PublishedAt,
    archived_at         AS ArchivedAt,
    is_deleted          AS IsDeleted,
    deleted_at          AS DeletedAt
FROM dbo.nh_wf_definitions
WHERE organization_id = @OrganizationId
  AND id = @Id
  AND is_deleted = 0;";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        return await connection.QuerySingleOrDefaultAsync<WorkflowDefinitionRecord>(
            scope.CreateCommand(
                sql,
                new { OrganizationId = organizationId, Id = id },
                cancellationToken));
    }

    public async Task<WorkflowDefinition?> GetPublishedVersionAsync(
        Guid organizationId,
        Guid id,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT
    id                  AS Id,
    organization_id     AS OrganizationId,
    name                AS Name,
    description         AS Description,
    version             AS Version,
    status              AS Status,
    start_step_id       AS StartStepId,
    definition          AS Definition,
    metadata            AS Metadata,
    created_at          AS CreatedAt,
    created_by          AS CreatedBy,
    updated_at          AS UpdatedAt,
    updated_by          AS UpdatedBy,
    published_at        AS PublishedAt,
    archived_at         AS ArchivedAt,
    is_deleted          AS IsDeleted,
    deleted_at          AS DeletedAt
FROM dbo.nh_wf_definitions
WHERE organization_id = @OrganizationId
  AND id = @Id
  AND status = 'Published'
  AND is_deleted = 0;";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        var row = await connection.QuerySingleOrDefaultAsync<WorkflowDefinitionRecord>(
            scope.CreateCommand(
                sql,
                new { OrganizationId = organizationId, Id = id },
                cancellationToken));

        return row is null ? null : WorkflowDefinitionMapper.FromRecord(row);
    }
    public async Task AddAsync(WorkflowDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        const string sql = @"
INSERT INTO dbo.nh_wf_definitions (
    id,
    organization_id,
    name,
    description,
    version,
    status,
    start_step_id,
    definition,
    metadata,
    created_at,
    created_by,
    updated_at,
    updated_by,
    published_at,
    archived_at,
    is_deleted,
    deleted_at
)
VALUES (
    @Id,
    @OrganizationId,
    @Name,
    @Description,
    @Version,
    @Status,
    @StartStepId,
    @Definition,
    @Metadata,
    @CreatedAt,
    @CreatedBy,
    @UpdatedAt,
    @UpdatedBy,
    @PublishedAt,
    @ArchivedAt,
    0,
    NULL
);";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        await connection.ExecuteAsync(
            scope.CreateCommand(
                sql,
                new
                {
                    definition.Id,
                    definition.OrganizationId,
                    definition.Name,
                    definition.Description,
                    definition.Version,
                    Status = definition.Status.ToString(),
                    definition.StartStepId,
                    Definition = WorkflowDefinitionMapper.SerializeDefinitionDocument(definition),
                    Metadata = WorkflowDefinitionMapper.SerializeMetadata(definition.Metadata),
                    definition.CreatedAt,
                    definition.CreatedBy,
                    UpdatedAt = (DateTimeOffset?)null,
                    UpdatedBy = (string?)null,
                    definition.PublishedAt,
                    definition.ArchivedAt
                },
                cancellationToken));
    }

    public async Task UpdateAsync(WorkflowDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        const string sql = @"
UPDATE dbo.nh_wf_definitions
SET
    name            = @Name,
    description     = @Description,
    version         = @Version,
    status          = @Status,
    start_step_id   = @StartStepId,
    definition      = @Definition,
    metadata        = @Metadata,
    updated_at      = @UpdatedAt,
    updated_by      = @UpdatedBy,
    published_at    = @PublishedAt,
    archived_at     = @ArchivedAt
WHERE organization_id = @OrganizationId
  AND id = @Id
  AND is_deleted = 0;";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        var rows = await connection.ExecuteAsync(
            scope.CreateCommand(
                sql,
                new
                {
                    definition.Id,
                    definition.OrganizationId,
                    definition.Name,
                    definition.Description,
                    definition.Version,
                    Status = definition.Status.ToString(),
                    definition.StartStepId,
                    Definition = WorkflowDefinitionMapper.SerializeDefinitionDocument(definition),
                    Metadata = WorkflowDefinitionMapper.SerializeMetadata(definition.Metadata),
                    UpdatedAt = DateTimeOffset.UtcNow,
                    UpdatedBy = (string?)null,
                    definition.PublishedAt,
                    definition.ArchivedAt
                },
                cancellationToken));

        if (rows == 0)
            throw new InvalidOperationException($"Workflow definition '{definition.Id}' was not found or is deleted.");
    }

    public async Task UpdateAsync(
        WorkflowDefinition definition,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        const string sql = @"
UPDATE dbo.nh_wf_definitions
SET
    name            = @Name,
    description     = @Description,
    version         = @Version,
    status          = @Status,
    start_step_id   = @StartStepId,
    definition      = @Definition,
    metadata        = @Metadata,
    updated_at      = @UpdatedAt,
    updated_by      = @UpdatedBy,
    published_at    = @PublishedAt,
    archived_at     = @ArchivedAt
WHERE organization_id = @OrganizationId
  AND id = @Id
  AND is_deleted = 0;";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        var rows = await connection.ExecuteAsync(
            scope.CreateCommand(
                sql,
                new
                {
                    definition.Id,
                    definition.OrganizationId,
                    definition.Name,
                    definition.Description,
                    definition.Version,
                    Status = definition.Status.ToString(),
                    definition.StartStepId,
                    Definition = WorkflowDefinitionMapper.SerializeDefinitionDocument(definition),
                    Metadata = WorkflowDefinitionMapper.SerializeMetadata(definition.Metadata),
                    UpdatedAt = DateTimeOffset.UtcNow,
                    UpdatedBy = updatedBy,
                    definition.PublishedAt,
                    definition.ArchivedAt
                },
                cancellationToken));

        if (rows == 0)
            throw new InvalidOperationException($"Workflow definition '{definition.Id}' was not found or is deleted.");
    }

    public async Task<bool> SoftDeleteAsync(
        Guid organizationId,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
UPDATE dbo.nh_wf_definitions
SET
    is_deleted = 1,
    deleted_at = @DeletedAt,
    updated_at = @DeletedAt
WHERE organization_id = @OrganizationId
  AND id = @Id
  AND is_deleted = 0;";

        using var scope = _sql.OpenScope();
        var connection = scope.Connection;
        var rows = await connection.ExecuteAsync(
            scope.CreateCommand(
                sql,
                new
                {
                    OrganizationId = organizationId,
                    Id = id,
                    DeletedAt = DateTimeOffset.UtcNow
                },
                cancellationToken));

        return rows > 0;
    }

    private static string ResolveSchemaScriptPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "sql", "008-workflow-automation-schema.sql"),
            Path.Combine(Directory.GetCurrentDirectory(), "sql", "008-workflow-automation-schema.sql")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            "Workflow automation schema script not found. Expected sql/008-workflow-automation-schema.sql.");
    }
}
