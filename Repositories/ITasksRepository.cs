using nexthire_api.Models.Tasks;

namespace nexthire_api.Repositories;

public interface ITasksRepository
{
    Task<IReadOnlyList<TaskDto>> GetListAsync(
        Guid orgId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? status,
        Guid? jobId,
        Guid? candidateId,
        Guid? applicationId,
        string? search,
        int page,
        int pageSize,
        string sort,
        string dir);

    Task<IReadOnlyList<TaskDto>> GetAllAsync(Guid orgId);
    Task<TaskDto?> GetByIdAsync(Guid orgId, Guid id);
    Task<IReadOnlyList<TaskDto>> GetByApplicationIdAsync(Guid orgId, Guid applicationId);

    Task<Guid?> GetTaskOrgIdAsync(Guid taskId);
    Task<Guid?> GetApplicationOrgIdAsync(Guid applicationId);
    Task<Guid?> GetUserOrgIdAsync(Guid userId);

    Task<TaskDto?> CreateAsync(Guid orgId, Guid id, CreateTaskRequest request, string statusNormalized);
    Task<bool> UpdateStatusAsync(Guid orgId, Guid id, string statusNormalized);
    Task<bool> DeleteAsync(Guid orgId, Guid id);
}

