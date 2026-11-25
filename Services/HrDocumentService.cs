using System.Collections.Concurrent;
using System.Text.Json;
using nexthire_api.DTOs;
using nexthire_api.DTOs.CoreHr;
using nexthire_api.Exceptions;
using nexthire_api.Repositories;
using nexthire_api.Security;

namespace nexthire_api.Services;

public class HrDocumentService : IHrDocumentService
{
    private const int DetailActivityTake = 25;

    // Orgs whose development demo data was already ensured by this process.
    private static readonly ConcurrentDictionary<Guid, bool> OrgInitialized = new();

    private readonly IHrDocumentRepository _repo;
    private readonly IEmployeeRepository _employees;
    private readonly IRoleRepository _roles;
    private readonly IResumeDocumentsUploader _uploader;
    private readonly IHostEnvironment _env;
    private readonly ILogger<HrDocumentService> _logger;

    public HrDocumentService(
        IHrDocumentRepository repo,
        IEmployeeRepository employees,
        IRoleRepository roles,
        IResumeDocumentsUploader uploader,
        IHostEnvironment env,
        ILogger<HrDocumentService> logger)
    {
        _repo = repo;
        _employees = employees;
        _roles = roles;
        _uploader = uploader;
        _env = env;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // Permissions / setup
    // -------------------------------------------------------------------------

    private async Task RequireAsync(HrActor actor, string capability)
    {
        var codes = await _roles.ListRoleCodesByNexaUserAsync(actor.OrganizationId, actor.UserId);
        if (!HrPermissions.Has(codes, capability))
            throw new HrAccessDeniedException(capability);
    }

    public async Task<IReadOnlyList<string>> GetCapabilitiesAsync(HrActor actor, CancellationToken cancellationToken = default)
    {
        var codes = await _roles.ListRoleCodesByNexaUserAsync(actor.OrganizationId, actor.UserId);
        return HrPermissions.ListFor(codes);
    }

    /// <summary>Development only: make sure the org has demo employees and documents so the hub is not empty.</summary>
    private async Task EnsureOrgReadyAsync(HrActor actor, CancellationToken cancellationToken)
    {
        if (!_env.IsDevelopment() || OrgInitialized.ContainsKey(actor.OrganizationId)) return;

        await _employees.EnsureOrgDefaultsAsync(actor.OrganizationId, cancellationToken);
        await _employees.SeedDemoEmployeesIfEmptyAsync(actor.OrganizationId, actor.UserId, true, cancellationToken);
        await _repo.SeedDemoDocumentsIfEmptyAsync(actor.OrganizationId, actor.UserId, true, cancellationToken);
        OrgInitialized[actor.OrganizationId] = true;
    }

    private static DateTime Today() => DateTime.UtcNow.Date;

    // -------------------------------------------------------------------------
    // Read
    // -------------------------------------------------------------------------

    public async Task<PagedResult<HrDocumentListItemDto>> ListAsync(HrActor actor, HrDocumentListQuery query, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, HrPermissions.View);

        if (!string.IsNullOrWhiteSpace(query.Category) && HrDocumentValidation.NormalizeCategory(query.Category) == null)
            throw EmployeeServiceException.Validation($"Category must be one of: {string.Join(", ", HrDocumentCategories.All)}.");
        if (!string.IsNullOrWhiteSpace(query.Status) && HrDocumentValidation.NormalizeDisplayStatus(query.Status) == null)
            throw EmployeeServiceException.Validation($"Status must be one of: {string.Join(", ", HrDocumentDisplayStatuses.All)}.");

        await EnsureOrgReadyAsync(actor, cancellationToken);
        return await _repo.ListAsync(actor.OrganizationId, query, Today(), cancellationToken);
    }

    public async Task<HrDocumentDetailDto?> GetAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, HrPermissions.View);

        var doc = await _repo.GetByIdAsync(actor.OrganizationId, id, Today(), cancellationToken);
        if (doc == null) return null;

        doc.Activity = await _repo.GetActivityAsync(actor.OrganizationId, doc.EmployeeId, doc.Id, DetailActivityTake, cancellationToken);
        return doc;
    }

    public async Task<HrDocumentSummaryDto> GetSummaryAsync(HrActor actor, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, HrPermissions.View);
        await EnsureOrgReadyAsync(actor, cancellationToken);
        return await _repo.SummaryAsync(actor.OrganizationId, Today(), cancellationToken);
    }

    public async Task<IReadOnlyList<HrDocumentCategoryCountDto>> GetCategoriesAsync(HrActor actor, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, HrPermissions.View);
        await EnsureOrgReadyAsync(actor, cancellationToken);
        return await _repo.CategoryCountsAsync(actor.OrganizationId, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Write
    // -------------------------------------------------------------------------

    public async Task<HrDocumentDetailDto> CreateAsync(HrActor actor, CreateHrDocumentRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, HrPermissions.ManageDocuments);

        var errors = HrDocumentValidation.ValidateCreate(request);
        if (errors.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", errors));
        await RequireEmployeeAsync(actor.OrganizationId, request.EmployeeId, cancellationToken);

        var row = new HrDocumentInsert(
            request.EmployeeId,
            HrDocumentValidation.NormalizeCategory(request.Category)!,
            request.Name!.Trim(),
            request.DocumentId,
            string.IsNullOrWhiteSpace(request.ExternalUrl) ? null : request.ExternalUrl.Trim(),
            HrDocumentValidation.WritableStatuses.FirstOrDefault(s => s.Equals(request.Status?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "Active",
            request.ExpiresAt?.Date,
            null,
            null);

        return await InsertAndLoadAsync(actor, row, cancellationToken);
    }

    public async Task<HrDocumentDetailDto> UploadAsync(HrActor actor, UploadHrDocumentForm form, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, HrPermissions.ManageDocuments);

        var file = form.File;
        var name = string.IsNullOrWhiteSpace(form.Name) ? HrDocumentValidation.DefaultNameFromFileName(file?.FileName) : form.Name;

        var errors = new List<string>();
        if (form.EmployeeId == Guid.Empty) errors.Add("employeeId is required.");
        if (file == null || file.Length == 0) errors.Add("Attach a file.");
        else if (file.Length > HrDocumentValidation.MaxUploadBytes) errors.Add("File is too large (10 MB max).");
        errors.AddRange(HrDocumentValidation.ValidateMetadata(form.Category, name, form.Status, null));
        if (errors.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", errors));

        await RequireEmployeeAsync(actor.OrganizationId, form.EmployeeId, cancellationToken);

        string uploadedId;
        try
        {
            uploadedId = await _uploader.UploadResumeAsync(actor.OrganizationId, file!, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            throw EmployeeServiceException.Validation(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            // The uploader reports both validation (size / type) and upstream failures as InvalidOperationException.
            _logger.LogWarning(ex, "HR document upload failed for organization {OrganizationId}.", actor.OrganizationId);
            var isValidation = ex.Message.Contains("not allowed", StringComparison.OrdinalIgnoreCase)
                               || ex.Message.Contains("too large", StringComparison.OrdinalIgnoreCase);
            throw isValidation
                ? EmployeeServiceException.Validation(ex.Message)
                : new EmployeeServiceException(StatusCodes.Status502BadGateway, "upload_failed", "The document could not be uploaded. Try again later.");
        }

        if (!Guid.TryParse(uploadedId, out var documentId))
            throw new EmployeeServiceException(StatusCodes.Status502BadGateway, "upload_failed", "The document service returned an unexpected document id.");

        var row = new HrDocumentInsert(
            form.EmployeeId,
            HrDocumentValidation.NormalizeCategory(form.Category)!,
            name!.Trim(),
            documentId,
            null,
            HrDocumentValidation.WritableStatuses.FirstOrDefault(s => s.Equals(form.Status?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "Active",
            form.ExpiresAt?.Date,
            string.IsNullOrWhiteSpace(file!.ContentType) ? null : file.ContentType.Trim(),
            file.Length);

        return await InsertAndLoadAsync(actor, row, cancellationToken);
    }

    private async Task<HrDocumentDetailDto> InsertAndLoadAsync(HrActor actor, HrDocumentInsert row, CancellationToken cancellationToken)
    {
        var id = await _repo.InsertAsync(actor.OrganizationId, row, actor.UserId, cancellationToken);
        await LogActivityAsync(actor, row.EmployeeId, "DocumentAdded", $"Document added: {row.Name}", id, cancellationToken);

        return await GetAsync(actor, id, cancellationToken)
               ?? throw EmployeeServiceException.NotFound("Document was created but could not be loaded.");
    }

    public async Task<HrDocumentDetailDto?> UpdateAsync(HrActor actor, Guid id, UpsertHrDocumentRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, HrPermissions.ManageDocuments);

        var errors = HrDocumentValidation.ValidateMetadata(request.Category, request.Name, request.Status, request.ExternalUrl);
        if (errors.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", errors));

        var existing = await _repo.GetByIdAsync(actor.OrganizationId, id, Today(), cancellationToken);
        if (existing == null) return null;

        string? status = string.IsNullOrWhiteSpace(request.Status)
            ? null
            : HrDocumentValidation.WritableStatuses.First(s => s.Equals(request.Status.Trim(), StringComparison.OrdinalIgnoreCase));

        var updated = await _repo.UpdateAsync(
            actor.OrganizationId,
            id,
            HrDocumentValidation.NormalizeCategory(request.Category)!,
            request.Name!.Trim(),
            status,
            request.ExpiresAt?.Date,
            string.IsNullOrWhiteSpace(request.ExternalUrl) ? null : request.ExternalUrl.Trim(),
            actor.UserId,
            cancellationToken);
        if (!updated) return null;

        await LogActivityAsync(actor, existing.EmployeeId, "DocumentUpdated", $"Document updated: {request.Name!.Trim()}", id, cancellationToken);
        return await GetAsync(actor, id, cancellationToken);
    }

    public async Task<bool> ArchiveAsync(HrActor actor, Guid id, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, HrPermissions.ManageDocuments);

        var existing = await _repo.GetByIdAsync(actor.OrganizationId, id, Today(), cancellationToken);
        if (existing == null) return false;

        var archived = await _repo.ArchiveAsync(actor.OrganizationId, id, actor.UserId, cancellationToken);
        if (archived)
            await LogActivityAsync(actor, existing.EmployeeId, "DocumentArchived", $"Document archived: {existing.Name}", id, cancellationToken);
        return archived;
    }

    // -------------------------------------------------------------------------
    // Document requests
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<DocumentRequestDto>> ListRequestsAsync(HrActor actor, string? status, Guid? employeeId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, HrPermissions.View);

        // Overdue is computed (Open + past due date), so Open/Overdue filters both read stored Open/Overdue rows.
        string[]? stored;
        string? displayFilter = null;
        var filter = status?.Trim();
        if (string.IsNullOrEmpty(filter) || filter.Equals("open", StringComparison.OrdinalIgnoreCase))
            stored = [HrDocumentRequestStatuses.Open, HrDocumentRequestStatuses.Overdue];
        else if (filter.Equals("all", StringComparison.OrdinalIgnoreCase))
            stored = null;
        else
        {
            displayFilter = new[]
            {
                HrDocumentRequestStatuses.Open, HrDocumentRequestStatuses.Fulfilled,
                HrDocumentRequestStatuses.Cancelled, HrDocumentRequestStatuses.Overdue
            }.FirstOrDefault(s => s.Equals(filter, StringComparison.OrdinalIgnoreCase));
            if (displayFilter == null)
                throw EmployeeServiceException.Validation("Status must be open, all, Open, Fulfilled, Cancelled or Overdue.");
            stored = displayFilter is HrDocumentRequestStatuses.Open or HrDocumentRequestStatuses.Overdue
                ? [HrDocumentRequestStatuses.Open, HrDocumentRequestStatuses.Overdue]
                : [displayFilter];
        }

        await EnsureOrgReadyAsync(actor, cancellationToken);
        var rows = await _repo.ListRequestsAsync(actor.OrganizationId, stored, employeeId, Today(), cancellationToken);
        return displayFilter == null ? rows : rows.Where(r => r.Status == displayFilter).ToList();
    }

    public async Task<DocumentRequestDto> CreateRequestAsync(HrActor actor, CreateDocumentRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, HrPermissions.ManageDocuments);

        var errors = HrDocumentValidation.ValidateDocumentRequest(request);
        if (errors.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", errors));
        await RequireEmployeeAsync(actor.OrganizationId, request.EmployeeId, cancellationToken);

        var title = request.Title!.Trim();
        var id = await _repo.CreateRequestAsync(
            actor.OrganizationId,
            request.EmployeeId,
            HrDocumentValidation.NormalizeCategory(request.Category)!,
            title,
            request.DueAt?.Date,
            string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            actor.UserId,
            cancellationToken);

        await _employees.AddActivityAsync(actor.OrganizationId, request.EmployeeId, "DocumentRequested",
            $"Document requested: {title}", JsonSerializer.Serialize(new { requestId = id }), actor.UserId, actor.Name, cancellationToken);

        return await _repo.GetRequestAsync(actor.OrganizationId, id, Today(), cancellationToken)
               ?? throw EmployeeServiceException.NotFound("Request was created but could not be loaded.");
    }

    public async Task<DocumentRequestDto?> FulfillRequestAsync(HrActor actor, Guid requestId, FulfillDocumentRequestRequest? request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, HrPermissions.ManageDocuments);

        var existing = await _repo.GetRequestAsync(actor.OrganizationId, requestId, Today(), cancellationToken);
        if (existing == null) return null;
        if (existing.Status is HrDocumentRequestStatuses.Fulfilled or HrDocumentRequestStatuses.Cancelled)
            throw EmployeeServiceException.Conflict($"This request is already {existing.Status.ToLowerInvariant()}.", "request_closed");

        var documentRowId = request?.DocumentRowId;
        if (documentRowId.HasValue)
        {
            var doc = await _repo.GetByIdAsync(actor.OrganizationId, documentRowId.Value, Today(), cancellationToken);
            if (doc == null || doc.EmployeeId != existing.EmployeeId)
                throw EmployeeServiceException.Validation("The document must exist and belong to the same employee as the request.");
        }

        if (!await _repo.FulfillRequestAsync(actor.OrganizationId, requestId, documentRowId, cancellationToken))
            throw EmployeeServiceException.Conflict("This request is already closed.", "request_closed");

        await _employees.AddActivityAsync(actor.OrganizationId, existing.EmployeeId, "DocumentRequestFulfilled",
            $"Document request fulfilled: {existing.Title}", JsonSerializer.Serialize(new { requestId }), actor.UserId, actor.Name, cancellationToken);

        return await _repo.GetRequestAsync(actor.OrganizationId, requestId, Today(), cancellationToken);
    }

    public async Task<DocumentRequestDto?> CancelRequestAsync(HrActor actor, Guid requestId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, HrPermissions.ManageDocuments);

        var existing = await _repo.GetRequestAsync(actor.OrganizationId, requestId, Today(), cancellationToken);
        if (existing == null) return null;
        if (existing.Status is HrDocumentRequestStatuses.Fulfilled or HrDocumentRequestStatuses.Cancelled)
            throw EmployeeServiceException.Conflict($"This request is already {existing.Status.ToLowerInvariant()}.", "request_closed");

        if (!await _repo.CancelRequestAsync(actor.OrganizationId, requestId, cancellationToken))
            throw EmployeeServiceException.Conflict("This request is already closed.", "request_closed");

        await _employees.AddActivityAsync(actor.OrganizationId, existing.EmployeeId, "DocumentRequestCancelled",
            $"Document request cancelled: {existing.Title}", JsonSerializer.Serialize(new { requestId }), actor.UserId, actor.Name, cancellationToken);

        return await _repo.GetRequestAsync(actor.OrganizationId, requestId, Today(), cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task RequireEmployeeAsync(Guid organizationId, Guid employeeId, CancellationToken cancellationToken)
    {
        var employee = await _employees.GetByIdAsync(organizationId, employeeId, cancellationToken);
        if (employee == null) throw EmployeeServiceException.NotFound("Employee not found.");
        if (employee.IsArchived) throw EmployeeServiceException.Conflict("Archived employees cannot receive new documents.", "employee_archived");
    }

    private Task LogActivityAsync(HrActor actor, Guid employeeId, string type, string summary, Guid documentRowId, CancellationToken cancellationToken) =>
        _employees.AddActivityAsync(actor.OrganizationId, employeeId, type, summary,
            JsonSerializer.Serialize(new { documentId = documentRowId }), actor.UserId, actor.Name, cancellationToken);
}
