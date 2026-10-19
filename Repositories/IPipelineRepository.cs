using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public interface IPipelineRepository
{
    Task<IReadOnlyList<PipelineStageDto>> GetStagesAsync(Guid orgId);
    Task<IReadOnlyList<PipelineStageDto>> EnsureDefaultStagesAsync(Guid orgId);
    Task<IReadOnlyList<PipelineStageSettingsDto>> GetStageSettingsAsync(Guid orgId);
    Task CreateStageAsync(Guid orgId, string name);
    Task RenameStageAsync(Guid orgId, Guid stageId, string name);
    Task ReorderStagesAsync(Guid orgId, IReadOnlyList<Guid> stageIds);
    Task DeleteStageAsync(Guid orgId, Guid stageId);
    Task<Guid?> GetFirstStageIdAsync(Guid orgId);
    Task<bool> StageExistsInOrgAsync(Guid orgId, Guid stageId);
    Task<bool> JobExistsInOrgAsync(Guid orgId, Guid jobId);
    Task<Guid?> GetJobOrgIdAsync(Guid jobId);
    Task<JobBotContextDto?> GetJobBotContextAsync(Guid jobId);
}

