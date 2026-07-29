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
    Task<JobAdDesignDto?> GetAdDesignAsync(Guid orgId, Guid jobId, CancellationToken cancellationToken = default);
    Task<JobAdDesignDto?> SaveAdDesignAsync(Guid orgId, Guid jobId, SaveJobAdDesignRequest request, CancellationToken cancellationToken = default);
    Task<bool> DeleteAdDesignAsync(Guid orgId, Guid jobId, CancellationToken cancellationToken = default);
    Task<(bool Deleted, bool NotFound, bool HasApplications)> DeleteJobAsync(Guid orgId, Guid id);
}

