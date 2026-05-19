using nexthire_api.DTOs;

namespace nexthire_api.Services;

public interface IApplicationsService
{
    Task<PagedResult<ApplicationListItemDto>> GetPagedAsync(Guid orgId,
        Guid? jobId,
        Guid? candidateId,
        Guid? stageId,
        string? status,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? search,
        int page,
        int pageSize);

    Task<ApplicationDetailDto?> GetDetailAsync(Guid orgId, Guid applicationId);

    Task<ApplicationListItemDto> CreateAsync(Guid orgId, Guid currentNhUserId, CreateApplicationRequestDto request);

    /// <summary>
    /// Kanban-style create: inserts application + initial stage_history (first pipeline stage unless overridden).
    /// </summary>
    Task<ApplicationCardDto> CreateKanbanApplicationAsync(
        Guid orgId,
        Guid nexaUserId,
        string? email,
        string? displayName,
        Guid jobId,
        Guid candidateId,
        Guid? currentStageId,
        string? status);

    Task<ApplicationListItemDto?> PatchAsync(Guid orgId, Guid currentNhUserId, Guid applicationId, UpdateApplicationRequestDto request);

    Task<ApplicationDetailDto?> MoveStageAsync(Guid orgId, Guid currentNhUserId, Guid applicationId, MoveStageRequestDto request);

    Task<ApplicationListItemDto?> DeleteAsync(Guid orgId, Guid applicationId);
}

