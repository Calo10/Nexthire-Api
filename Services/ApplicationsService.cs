using nexthire_api.DTOs;
using nexthire_api.Repositories;

namespace nexthire_api.Services;

public class ApplicationsService : IApplicationsService
{
    private readonly IApplicationsRepository _repo;
    private readonly IPipelineRepository _pipelineRepo;
    private readonly ILogger<ApplicationsService> _logger;

    public ApplicationsService(
        IApplicationsRepository repo,
        IPipelineRepository pipelineRepo,
        ILogger<ApplicationsService> logger)
    {
        _repo = repo;
        _pipelineRepo = pipelineRepo;
        _logger = logger;
    }

    public Task<PagedResult<ApplicationListItemDto>> GetPagedAsync(
        Guid orgId,
        Guid? jobId,
        Guid? candidateId,
        Guid? stageId,
        string? status,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? search,
        int page,
        int pageSize)
    {
        return _repo.GetPagedAsync(orgId, jobId, candidateId, stageId, status, from, to, search, page, pageSize);
    }

    public async Task<ApplicationDetailDto?> GetDetailAsync(Guid orgId, Guid applicationId)
    {
        var app = await _repo.GetByIdAsync(orgId, applicationId);
        if (app == null)
            return null;

        var history = await _repo.GetStageHistoryAsync(orgId, applicationId, limit: 50);

        return new ApplicationDetailDto
        {
            Application = app,
            StageHistory = history
        };
    }

    public async Task<ApplicationListItemDto> CreateAsync(Guid orgId, Guid currentNhUserId, CreateApplicationRequestDto request)
    {
        // Validate foreign keys belong to org.
        if (!await _repo.JobExistsInOrgAsync(orgId, request.JobId))
            throw new ArgumentException("jobId not found in this organization");
        if (!await _repo.CandidateExistsInOrgAsync(orgId, request.CandidateId))
            throw new ArgumentException("candidateId not found in this organization");
        if (!await _repo.StageExistsInOrgAsync(orgId, request.CurrentStageId))
            throw new ArgumentException("currentStageId not found in this organization");

        var created = await _repo.CreateAsync(orgId, request);

        // Initial stage history row
        await _repo.InsertStageHistoryAsync(orgId, created.ApplicationId, fromStageId: null, toStageId: request.CurrentStageId, movedByUserId: currentNhUserId);

        return created;
    }

    public async Task<ApplicationCardDto> CreateKanbanApplicationAsync(
        Guid orgId,
        Guid nexaUserId,
        string? email,
        string? displayName,
        Guid jobId,
        Guid candidateId,
        Guid? currentStageId,
        string? status)
    {
        await _pipelineRepo.EnsureDefaultStagesAsync(orgId);
        var firstStageId = await _pipelineRepo.GetFirstStageIdAsync(orgId);
        if (!firstStageId.HasValue)
            throw new InvalidOperationException("Unable to resolve pipeline stages for org");

        var stageId = currentStageId ?? firstStageId.Value;
        if (!await _pipelineRepo.StageExistsInOrgAsync(orgId, stageId))
            throw new ArgumentException("currentStageId not found in this organization");

        if (!await _repo.JobExistsInOrgAsync(orgId, jobId))
            throw new ArgumentException("jobId not found in this organization");
        if (!await _repo.CandidateExistsInOrgAsync(orgId, candidateId))
            throw new ArgumentException("candidateId not found in this organization");

        var nhUserId = await _repo.GetOrCreateNhUserIdAsync(orgId, nexaUserId, email, displayName);
        var statusValue = string.IsNullOrWhiteSpace(status) ? "active" : status!;

        var createdCard = await _repo.CreateKanbanAsync(orgId, jobId, candidateId, stageId, statusValue);
        await _repo.InsertStageHistoryAsync(orgId, createdCard.Id, fromStageId: null, toStageId: stageId, movedByUserId: nhUserId);

        return createdCard;
    }

    public async Task<ApplicationListItemDto?> PatchAsync(Guid orgId, Guid currentNhUserId, Guid applicationId, UpdateApplicationRequestDto request)
    {
        if (request.CurrentStageId.HasValue && !await _repo.StageExistsInOrgAsync(orgId, request.CurrentStageId.Value))
            throw new ArgumentException("currentStageId not found in this organization");

        var (updated, prevStage) = await _repo.UpdateAsync(orgId, applicationId, request);
        if (updated == null)
            return null;

        if (request.CurrentStageId.HasValue && prevStage.HasValue && prevStage.Value != request.CurrentStageId.Value)
        {
            await _repo.InsertStageHistoryAsync(orgId, applicationId, fromStageId: prevStage.Value, toStageId: request.CurrentStageId.Value, movedByUserId: currentNhUserId);
        }

        return updated;
    }

    public async Task<ApplicationDetailDto?> MoveStageAsync(Guid orgId, Guid currentNhUserId, Guid applicationId, MoveStageRequestDto request)
    {
        if (!await _repo.StageExistsInOrgAsync(orgId, request.ToStageId))
            throw new ArgumentException("toStageId not found in this organization");

        // Move stage via patch
        var updated = await PatchAsync(orgId, currentNhUserId, applicationId, new UpdateApplicationRequestDto
        {
            CurrentStageId = request.ToStageId
        });

        if (updated == null)
            return null;

        // Optional note creation: kept out of scope until schema is confirmed.
        if (!string.IsNullOrWhiteSpace(request.Note))
        {
            _logger.LogInformation("Move-stage note provided (not persisted due to unknown schema). ApplicationId: {ApplicationId}", applicationId);
        }

        // Return detail (includes stage history)
        return await GetDetailAsync(orgId, applicationId);
    }

    public Task<ApplicationListItemDto?> DeleteAsync(Guid orgId, Guid applicationId)
    {
        // Soft delete recommended: archive.
        return _repo.SoftArchiveAsync(orgId, applicationId);
    }
}

