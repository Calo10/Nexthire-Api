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
        ValidateLanguage(createJobDto.Language);
        return await _repo.CreateAsync(orgId, createdByUserId, createJobDto);
    }

    public async Task<JobDto?> UpdateJobAsync(Guid orgId, Guid id, UpdateJobDto updateJobDto)
    {
        ValidateLanguage(updateJobDto.Language);
        return await _repo.UpdateAsync(orgId, id, updateJobDto);
    }

    public async Task<(bool Deleted, bool NotFound, bool HasApplications)> DeleteJobAsync(Guid orgId, Guid id)
    {
        var existing = await _repo.GetByIdAsync(orgId, id);
        if (existing == null)
            return (false, true, false);

        if (await _repo.HasApplicationsAsync(orgId, id))
            return (false, false, true);

        var deleted = await _repo.DeleteAsync(orgId, id);
        return (deleted, !deleted, false);
    }

    private static void ValidateLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return;

        if (!JobLanguageCodes.Allowed.Contains(language.Trim()))
            throw new ArgumentException("language must be 'es' or 'en'.");
    }
}

