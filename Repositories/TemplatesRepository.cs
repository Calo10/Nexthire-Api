using Dapper;
using nexthire_api.Data;
using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public class TemplatesRepository : ITemplatesRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<TemplatesRepository> _logger;

    public TemplatesRepository(IDbConnectionFactory connectionFactory, ILogger<TemplatesRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TemplateListItemDto>> ListAsync(Guid orgId, string? channel)
    {
        const string sql = @"
            SELECT
                id AS Id,
                name AS Name,
                channel AS Channel,
                subject AS Subject,
                CAST(is_active AS bit) AS IsActive,
                created_at AS CreatedAt
            FROM message_templates
            WHERE org_id = @orgId
              AND (@channel IS NULL OR channel = @channel)
            ORDER BY created_at DESC, id DESC;";

        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<TemplateListItemDto>(sql, new { orgId, channel });
        return rows.ToList();
    }

    public async Task<TemplateDto?> GetAsync(Guid orgId, Guid id)
    {
        const string sql = @"
            SELECT
                id AS Id,
                name AS Name,
                channel AS Channel,
                subject AS Subject,
                body AS Body,
                CAST(is_active AS bit) AS IsActive,
                created_at AS CreatedAt
            FROM message_templates
            WHERE org_id = @orgId AND id = @id;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<TemplateDto>(sql, new { orgId, id });
    }

    public async Task<TemplateDto> CreateAsync(Guid orgId, CreateTemplateRequestDto request, string channelNormalized, string? subjectNormalized, bool isActive)
    {
        const string sql = @"
            INSERT INTO message_templates (id, org_id, name, channel, subject, body, is_active, created_at)
            OUTPUT
                INSERTED.id AS Id,
                INSERTED.name AS Name,
                INSERTED.channel AS Channel,
                INSERTED.subject AS Subject,
                INSERTED.body AS Body,
                CAST(INSERTED.is_active AS bit) AS IsActive,
                INSERTED.created_at AS CreatedAt
            VALUES (
                @Id,
                @OrgId,
                @Name,
                @Channel,
                @Subject,
                @Body,
                @IsActive,
                TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            );";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QuerySingleAsync<TemplateDto>(sql, new
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Name = request.Name.Trim(),
            Channel = channelNormalized,
            Subject = subjectNormalized,
            Body = request.Body,
            IsActive = isActive
        });
    }

    public async Task<TemplateDto?> UpdateAsync(Guid orgId, Guid id, UpdateTemplateRequestDto request, string channelNormalized, string? subjectNormalized, bool isActive)
    {
        // NOTE: channel is not editable per spec, but we keep the signature so controller can pass canonical channel.
        const string sql = @"
            UPDATE message_templates
            SET
                name = @Name,
                subject = @Subject,
                body = @Body,
                is_active = @IsActive
            WHERE org_id = @OrgId AND id = @Id;

            SELECT
                id AS Id,
                name AS Name,
                channel AS Channel,
                subject AS Subject,
                body AS Body,
                CAST(is_active AS bit) AS IsActive,
                created_at AS CreatedAt
            FROM message_templates
            WHERE org_id = @OrgId AND id = @Id;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<TemplateDto>(sql, new
        {
            OrgId = orgId,
            Id = id,
            Name = request.Name.Trim(),
            Subject = subjectNormalized,
            Body = request.Body,
            IsActive = isActive,
            Channel = channelNormalized
        });
    }

    public async Task<bool> DeleteAsync(Guid orgId, Guid id)
    {
        const string sql = @"DELETE FROM message_templates WHERE org_id = @orgId AND id = @id;";

        using var connection = _connectionFactory.CreateConnection();
        var affected = await connection.ExecuteAsync(sql, new { orgId, id });
        return affected > 0;
    }
}

