using nexthire_api.DTOs;

namespace nexthire_api.Services;

public interface IJobService
{
    Task<IEnumerable<JobDto>> GetAllJobsAsync(Guid orgId);
    Task<JobDto?> GetJobByIdAsync(Guid orgId, Guid id);
    Task<IEnumerable<JobDto>> GetPublicOpenJobsAsync(Guid orgId);
    Task<JobDto?> GetPublicOpenJobByIdAsync(Guid orgId, Guid id);
    Task<JobDto> CreateJobAsync(Guid orgId, Guid createdByUserId, CreateJobDto createJobDto);
    Task<JobDto?> UpdateJobAsync(Guid orgId, Guid id, UpdateJobDto updateJobDto);
    Task<(bool Deleted, bool NotFound, bool HasApplications)> DeleteJobAsync(Guid orgId, Guid id);
}

