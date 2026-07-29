using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public interface IJobRepository
{
    Task EnsureAdDesignSchemaAsync(CancellationToken cancellationToken = default);
    Task<IEnumerable<JobDto>> GetAllAsync(Guid orgId);
    Task<JobDto?> GetByIdAsync(Guid orgId, Guid id);
    Task<IEnumerable<JobDto>> GetPublicOpenAsync(Guid orgId);
    Task<JobDto?> GetPublicOpenByIdAsync(Guid orgId, Guid id);
    Task<JobDto> CreateAsync(Guid orgId, Guid createdByUserId, CreateJobDto dto);
    Task<JobDto?> UpdateAsync(Guid orgId, Guid id, UpdateJobDto dto);
    Task<JobAdDesignDto?> GetAdDesignAsync(Guid orgId, Guid jobId, CancellationToken cancellationToken = default);
    Task<JobAdDesignDto?> SaveAdDesignAsync(Guid orgId, Guid jobId, SaveJobAdDesignRequest request, CancellationToken cancellationToken = default);
    Task<bool> DeleteAdDesignAsync(Guid orgId, Guid jobId, CancellationToken cancellationToken = default);
    Task<bool> HasApplicationsAsync(Guid orgId, Guid jobId);
    Task<bool> DeleteAsync(Guid orgId, Guid id);
}

