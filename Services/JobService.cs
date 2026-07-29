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

    public Task<JobAdDesignDto?> GetAdDesignAsync(Guid orgId, Guid jobId, CancellationToken cancellationToken = default)
        => _repo.GetAdDesignAsync(orgId, jobId, cancellationToken);

    public async Task<JobAdDesignDto?> SaveAdDesignAsync(
        Guid orgId,
        Guid jobId,
        SaveJobAdDesignRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var raw = (request.ImageBase64 ?? string.Empty).Trim();
        if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = raw.IndexOf(',');
            if (comma >= 0)
                raw = raw[(comma + 1)..];
        }

        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("ImageBase64 is required.");

        try
        {
            _ = Convert.FromBase64String(raw);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("ImageBase64 is invalid.", ex);
        }

        request.ImageBase64 = raw;
        if (string.IsNullOrWhiteSpace(request.ImageContentType))
            request.ImageContentType = "image/png";

        return await _repo.SaveAdDesignAsync(orgId, jobId, request, cancellationToken);
    }

    public Task<bool> DeleteAdDesignAsync(Guid orgId, Guid jobId, CancellationToken cancellationToken = default)
        => _repo.DeleteAdDesignAsync(orgId, jobId, cancellationToken);

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

