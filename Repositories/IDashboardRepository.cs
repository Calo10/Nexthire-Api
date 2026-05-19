using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public interface IDashboardRepository
{
    Task<DashboardSummaryDto> GetSummaryAsync(Guid orgId, DateTimeOffset? from, DateTimeOffset? to, Guid? jobId);
    Task<IEnumerable<ApplicationsByStageDto>> GetApplicationsByStageAsync(Guid orgId, DateTimeOffset? from, DateTimeOffset? to, Guid? jobId);
    Task<IEnumerable<ActivityTrendDto>> GetActivityTrendAsync(Guid orgId, DateTimeOffset? from, DateTimeOffset? to, Guid? jobId);
    Task<IEnumerable<MyJobDto>> GetMyJobsAsync(Guid orgId, DateTimeOffset? from, DateTimeOffset? to);
    Task<IEnumerable<RecentCandidateDto>> GetRecentCandidatesAsync(Guid orgId, int limit);
    Task<IEnumerable<UpcomingTaskDto>> GetUpcomingTasksAsync(Guid orgId, int limit);
}
