using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public interface IJobRepository
{
    Task<IEnumerable<JobDto>> GetAllAsync(Guid orgId);
    Task<JobDto?> GetByIdAsync(Guid orgId, Guid id);
    Task<IEnumerable<JobDto>> GetPublicOpenAsync(Guid orgId);
    Task<JobDto?> GetPublicOpenByIdAsync(Guid orgId, Guid id);
    Task<JobDto> CreateAsync(Guid orgId, Guid createdByUserId, CreateJobDto dto);
    Task<JobDto?> UpdateAsync(Guid orgId, Guid id, UpdateJobDto dto);
    Task<bool> HasApplicationsAsync(Guid orgId, Guid jobId);
    Task<bool> DeleteAsync(Guid orgId, Guid id);
}

