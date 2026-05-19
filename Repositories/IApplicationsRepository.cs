using System.Data;
using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public interface IApplicationsRepository
{
    Task<PagedResult<ApplicationListItemDto>> GetPagedAsync(
        Guid orgId,
        Guid? jobId,
        Guid? candidateId,
        Guid? stageId,
        string? status,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? search,
        int page,
        int pageSize);

    Task<ApplicationListItemDto?> GetByIdAsync(Guid orgId, Guid id);

    Task<IReadOnlyList<ApplicationStageHistoryItemDto>> GetStageHistoryAsync(Guid orgId, Guid applicationId, int limit);

    Task<bool> JobExistsInOrgAsync(Guid orgId, Guid jobId);
    Task<bool> CandidateExistsInOrgAsync(Guid orgId, Guid candidateId);
    Task<bool> StageExistsInOrgAsync(Guid orgId, Guid stageId);

    Task<Guid> GetOrCreateNhUserIdAsync(Guid orgId, Guid nexaUserId, string? email, string? displayName);

    Task<ApplicationListItemDto> CreateAsync(Guid orgId, CreateApplicationRequestDto request);

    Task<(ApplicationListItemDto? Updated, Guid? PreviousStageId)> UpdateAsync(Guid orgId, Guid applicationId, UpdateApplicationRequestDto request);

    Task InsertStageHistoryAsync(Guid orgId, Guid applicationId, Guid? fromStageId, Guid toStageId, Guid movedByUserId, IDbTransaction? transaction = null);

    Task<ApplicationListItemDto?> SoftArchiveAsync(Guid orgId, Guid applicationId);

    // Kanban (minimal)
    Task<IReadOnlyList<ApplicationCardDto>> GetKanbanCardsAsync(Guid orgId, Guid jobId);
    Task<ApplicationCardDto?> GetKanbanCardByIdAsync(Guid orgId, Guid applicationId);
    Task<ApplicationCardDto> CreateKanbanAsync(Guid orgId, Guid jobId, Guid candidateId, Guid currentStageId, string status, IDbTransaction? transaction = null);
    Task<ApplicationCardDto?> MoveKanbanAsync(Guid orgId, Guid applicationId, Guid toStageId, Guid movedByUserId);

    Task<bool> ExistsForJobCandidateAsync(Guid orgId, Guid jobId, Guid candidateId, IDbTransaction? transaction = null);

    Task<Guid?> FindApplicationIdForJobCandidateAsync(Guid orgId, Guid jobId, Guid candidateId, IDbTransaction? transaction = null);
}

