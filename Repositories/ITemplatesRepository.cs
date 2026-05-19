using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public interface ITemplatesRepository
{
    Task<IReadOnlyList<TemplateListItemDto>> ListAsync(Guid orgId, string? channel);
    Task<TemplateDto?> GetAsync(Guid orgId, Guid id);
    Task<TemplateDto> CreateAsync(Guid orgId, CreateTemplateRequestDto request, string channelNormalized, string? subjectNormalized, bool isActive);
    Task<TemplateDto?> UpdateAsync(Guid orgId, Guid id, UpdateTemplateRequestDto request, string channelNormalized, string? subjectNormalized, bool isActive);
    Task<bool> DeleteAsync(Guid orgId, Guid id);
}

