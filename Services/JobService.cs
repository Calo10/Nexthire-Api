using nexthire_api.DTOs;
using nexthire_api.Repositories;

namespace nexthire_api.Services;

public class JobService : IJobService
{
    private readonly IJobRepository _repo;

    public JobService(IJobRepository repo)
    {
        _repo = repo;
    }

    public async Task<IEnumerable<JobDto>> GetAllJobsAsync(Guid orgId)
    {
        return await _repo.GetAllAsync(orgId);
    }

    public async Task<JobDto?> GetJobByIdAsync(Guid orgId, Guid id)
    {
        return await _repo.GetByIdAsync(orgId, id);
    }

    public async Task<IEnumerable<JobDto>> GetPublicOpenJobsAsync(Guid orgId)
    {
        return await _repo.GetPublicOpenAsync(orgId);
    }

    public async Task<JobDto?> GetPublicOpenJobByIdAsync(Guid orgId, Guid id)
    {
        return await _repo.GetPublicOpenByIdAsync(orgId, id);
    }

    public async Task<JobDto> CreateJobAsync(Guid orgId, Guid createdByUserId, CreateJobDto createJobDto)
    {
        return await _repo.CreateAsync(orgId, createdByUserId, createJobDto);
    }

    public async Task<JobDto?> UpdateJobAsync(Guid orgId, Guid id, UpdateJobDto updateJobDto)
    {
        return await _repo.UpdateAsync(orgId, id, updateJobDto);
    }

    public async Task<bool> DeleteJobAsync(Guid orgId, Guid id)
    {
        return await _repo.DeleteAsync(orgId, id);
    }
}

