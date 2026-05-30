using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public interface IPipelineRepository
{
    Task<IReadOnlyList<PipelineStageDto>> GetStagesAsync(Guid orgId);
    Task<IReadOnlyList<PipelineStageDto>> EnsureDefaultStagesAsync(Guid orgId);
    Task<Guid?> GetFirstStageIdAsync(Guid orgId);
    Task<bool> StageExistsInOrgAsync(Guid orgId, Guid stageId);
    Task<bool> JobExistsInOrgAsync(Guid orgId, Guid jobId);
    Task<Guid?> GetJobOrgIdAsync(Guid jobId);
}

