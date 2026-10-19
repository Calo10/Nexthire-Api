using nexthire_api.DTOs;

namespace nexthire_api.Services;

public interface IPipelineStageSettingsService
{
    Task<IReadOnlyList<PipelineStageSettingsDto>> ListAsync(Guid orgId, Guid nexaUserId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PipelineStageSettingsDto>> CreateAsync(Guid orgId, Guid nexaUserId, string name, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PipelineStageSettingsDto>> RenameAsync(Guid orgId, Guid nexaUserId, Guid stageId, string name, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PipelineStageSettingsDto>> ReorderAsync(Guid orgId, Guid nexaUserId, IReadOnlyList<Guid> stageIds, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PipelineStageSettingsDto>> DeleteAsync(Guid orgId, Guid nexaUserId, Guid stageId, CancellationToken cancellationToken = default);
}
