using System.Text.RegularExpressions;
using nexthire_api.DTOs;
using nexthire_api.Repositories;

namespace nexthire_api.Services;

public interface IJobBotQuestionService
{
    Task<IReadOnlyList<JobBotQuestionDto>> ListAsync(Guid orgId, Guid jobId, bool includeInactive);
    Task<JobBotQuestionDto?> GetAsync(Guid orgId, Guid jobId, Guid questionId);
    Task<JobBotQuestionDto> CreateAsync(Guid orgId, Guid jobId, CreateJobBotQuestionRequestDto dto);
    Task<JobBotQuestionDto?> UpdateAsync(Guid orgId, Guid jobId, Guid questionId, UpdateJobBotQuestionRequestDto dto);
    Task<bool> DeleteAsync(Guid orgId, Guid jobId, Guid questionId);
}

public partial class JobBotQuestionService : IJobBotQuestionService
{
    private readonly IJobBotQuestionRepository _repository;
    private readonly IPipelineRepository _pipeline;

    public JobBotQuestionService(IJobBotQuestionRepository repository, IPipelineRepository pipeline)
    {
        _repository = repository;
        _pipeline = pipeline;
    }

    public async Task<IReadOnlyList<JobBotQuestionDto>> ListAsync(Guid orgId, Guid jobId, bool includeInactive)
    {
        await EnsureJobExistsAsync(orgId, jobId);
        return await _repository.ListByJobAsync(orgId, jobId, includeInactive);
    }

    public async Task<JobBotQuestionDto?> GetAsync(Guid orgId, Guid jobId, Guid questionId)
    {
        await EnsureJobExistsAsync(orgId, jobId);
        return await _repository.GetAsync(orgId, jobId, questionId);
    }

    public async Task<JobBotQuestionDto> CreateAsync(Guid orgId, Guid jobId, CreateJobBotQuestionRequestDto dto)
    {
        await EnsureJobExistsAsync(orgId, jobId);

        var questionKey = NormalizeQuestionKey(dto.QuestionKey);
        ValidateQuestionKey(questionKey);

        var answerType = JobBotQuestionAnswerTypes.NormalizeOrNull(dto.AnswerType)
            ?? throw new ArgumentException("Invalid answerType. Allowed: text, number, yes_no, file.");

        if (await _repository.QuestionKeyExistsAsync(jobId, questionKey))
            throw new ArgumentException($"questionKey '{questionKey}' already exists for this job.");

        var sortOrder = dto.SortOrder ?? await _repository.GetNextSortOrderAsync(orgId, jobId);
        if (sortOrder < 0)
            throw new ArgumentException("sortOrder must be zero or greater.");

        var isRequired = dto.IsRequired ?? true;
        var isActive = dto.IsActive ?? true;

        return await _repository.CreateAsync(orgId, jobId, questionKey, dto, answerType, sortOrder, isRequired, isActive);
    }

    public async Task<JobBotQuestionDto?> UpdateAsync(
        Guid orgId,
        Guid jobId,
        Guid questionId,
        UpdateJobBotQuestionRequestDto dto)
    {
        await EnsureJobExistsAsync(orgId, jobId);

        var existing = await _repository.GetAsync(orgId, jobId, questionId);
        if (existing == null)
            return null;

        var answerType = JobBotQuestionAnswerTypes.NormalizeOrNull(dto.AnswerType)
            ?? throw new ArgumentException("Invalid answerType. Allowed: text, number, yes_no, file.");

        var sortOrder = dto.SortOrder ?? existing.SortOrder;
        if (sortOrder < 0)
            throw new ArgumentException("sortOrder must be zero or greater.");

        var isRequired = dto.IsRequired ?? existing.IsRequired;
        var isActive = dto.IsActive ?? existing.IsActive;

        return await _repository.UpdateAsync(
            orgId,
            jobId,
            questionId,
            dto,
            answerType,
            sortOrder,
            isRequired,
            isActive);
    }

    public async Task<bool> DeleteAsync(Guid orgId, Guid jobId, Guid questionId)
    {
        await EnsureJobExistsAsync(orgId, jobId);
        return await _repository.DeleteAsync(orgId, jobId, questionId);
    }

    private async Task EnsureJobExistsAsync(Guid orgId, Guid jobId)
    {
        if (!await _pipeline.JobExistsInOrgAsync(orgId, jobId))
            throw new KeyNotFoundException($"Job with ID {jobId} not found.");
    }

    private static string NormalizeQuestionKey(string value) =>
        value.Trim().ToLowerInvariant();

    private static void ValidateQuestionKey(string questionKey)
    {
        if (!QuestionKeyRegex().IsMatch(questionKey))
            throw new ArgumentException("questionKey must use lowercase letters, numbers or underscores (2-80 chars).");
    }

    [GeneratedRegex("^[a-z0-9_]{2,80}$", RegexOptions.CultureInvariant)]
    private static partial Regex QuestionKeyRegex();
}
