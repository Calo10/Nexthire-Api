using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public interface IJobBotQuestionRepository
{
    Task<IReadOnlyList<JobBotQuestionDto>> ListByJobAsync(Guid orgId, Guid jobId, bool includeInactive);
    Task<JobBotQuestionDto?> GetAsync(Guid orgId, Guid jobId, Guid questionId);
    Task<bool> QuestionKeyExistsAsync(Guid jobId, string questionKey, Guid? excludeQuestionId = null);
    Task<int> GetNextSortOrderAsync(Guid orgId, Guid jobId);
    Task<JobBotQuestionDto> CreateAsync(Guid orgId, Guid jobId, string questionKey, CreateJobBotQuestionRequestDto dto, string answerType, int sortOrder, bool isRequired, bool isActive);
    Task<JobBotQuestionDto?> UpdateAsync(Guid orgId, Guid jobId, Guid questionId, UpdateJobBotQuestionRequestDto dto, string answerType, int sortOrder, bool isRequired, bool isActive);
    Task<bool> DeleteAsync(Guid orgId, Guid jobId, Guid questionId);
}
