using nexthire_api.Models.Notes;

namespace nexthire_api.Repositories;

public interface INotesRepository
{
    Task<IReadOnlyList<NoteDto>> GetByApplicationIdAsync(Guid orgId, Guid applicationId, int limit);
    Task<NoteDto?> GetByIdAsync(Guid orgId, Guid id);
    Task<NoteDto> CreateAsync(Guid orgId, Guid id, Guid applicationId, string body, Guid? createdByUserId);
    Task<bool> UpdateAsync(Guid orgId, Guid id, string body);
    Task<bool> DeleteAsync(Guid orgId, Guid id);
}

