using nexthire_api.DTOs;
using nexthire_api.Exceptions;
using nexthire_api.Repositories;

namespace nexthire_api.Services;

public class PipelineStageSettingsService : IPipelineStageSettingsService
{
    public const int MaxNameLength = 60;

    private readonly IPipelineRepository _pipeline;
    private readonly IOrgMembershipAuthorizationService _orgAuth;

    public PipelineStageSettingsService(
        IPipelineRepository pipeline,
        IOrgMembershipAuthorizationService orgAuth)
    {
        _pipeline = pipeline;
        _orgAuth = orgAuth;
    }

    public async Task<IReadOnlyList<PipelineStageSettingsDto>> ListAsync(
        Guid orgId,
        Guid nexaUserId,
        CancellationToken cancellationToken = default)
    {
        await _orgAuth.EnsureOwnerOrAdminAsync(orgId, nexaUserId, cancellationToken);
        await _pipeline.EnsureDefaultStagesAsync(orgId);
        return Enrich(await _pipeline.GetStageSettingsAsync(orgId));
    }

    public async Task<IReadOnlyList<PipelineStageSettingsDto>> CreateAsync(
        Guid orgId,
        Guid nexaUserId,
        string name,
        CancellationToken cancellationToken = default)
    {
        await _orgAuth.EnsureOwnerOrAdminAsync(orgId, nexaUserId, cancellationToken);
        await _pipeline.CreateStageAsync(orgId, NormalizeName(name));
        return Enrich(await _pipeline.GetStageSettingsAsync(orgId));
    }

    public async Task<IReadOnlyList<PipelineStageSettingsDto>> RenameAsync(
        Guid orgId,
        Guid nexaUserId,
        Guid stageId,
        string name,
        CancellationToken cancellationToken = default)
    {
        await _orgAuth.EnsureOwnerOrAdminAsync(orgId, nexaUserId, cancellationToken);
        await _pipeline.RenameStageAsync(orgId, stageId, NormalizeName(name));
        return Enrich(await _pipeline.GetStageSettingsAsync(orgId));
    }

    public async Task<IReadOnlyList<PipelineStageSettingsDto>> ReorderAsync(
        Guid orgId,
        Guid nexaUserId,
        IReadOnlyList<Guid> stageIds,
        CancellationToken cancellationToken = default)
    {
        await _orgAuth.EnsureOwnerOrAdminAsync(orgId, nexaUserId, cancellationToken);
        await _pipeline.ReorderStagesAsync(orgId, stageIds);
        return Enrich(await _pipeline.GetStageSettingsAsync(orgId));
    }

    public async Task<IReadOnlyList<PipelineStageSettingsDto>> DeleteAsync(
        Guid orgId,
        Guid nexaUserId,
        Guid stageId,
        CancellationToken cancellationToken = default)
    {
        await _orgAuth.EnsureOwnerOrAdminAsync(orgId, nexaUserId, cancellationToken);
        await _pipeline.DeleteStageAsync(orgId, stageId);
        return Enrich(await _pipeline.GetStageSettingsAsync(orgId));
    }

    internal static string NormalizeName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            throw new PipelineStageException(400, "name_required", "Column name is required.");
        if (trimmed.Length > MaxNameLength)
            throw new PipelineStageException(400, "name_too_long", "Column name must be 60 characters or fewer.");
        return trimmed;
    }

    internal static IReadOnlyList<PipelineStageSettingsDto> Enrich(IReadOnlyList<PipelineStageSettingsDto> rows)
    {
        var stageCount = rows.Count;
        foreach (var row in rows)
        {
            row.DeleteBlockedReason = row.ApplicationCount > 0
                ? "in_use"
                : row.HistoryCount > 0
                    ? "history"
                    : stageCount <= 1
                        ? "last_stage"
                        : null;
            row.CanDelete = row.DeleteBlockedReason is null;
        }

        return rows;
    }
}
