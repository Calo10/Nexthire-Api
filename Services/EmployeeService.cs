using System.Collections.Concurrent;
using System.Data.SqlClient;
using System.Globalization;
using System.Text;
using System.Text.Json;
using nexthire_api.DTOs;
using nexthire_api.DTOs.CoreHr;
using nexthire_api.Exceptions;
using nexthire_api.Helpers;
using nexthire_api.Repositories;
using nexthire_api.Security;

namespace nexthire_api.Services;

public class EmployeeService : IEmployeeService
{
    private const int ExportRowCap = 5000;
    private const int ImportRowCap = 2000;
    private const int ExportPageSize = 500;

    // Orgs whose HR defaults (employment types, dev demo data) were already ensured by this process.
    private static readonly ConcurrentDictionary<Guid, bool> OrgInitialized = new();

    private static readonly string[] ImportHeaders =
    [
        "FirstName", "LastName", "WorkEmail", "EmployeeNumber", "Department",
        "JobTitle", "Location", "EmploymentStatus", "EmploymentType", "HireDate"
    ];

    private static readonly string[] ImportDateFormats =
        ["yyyy-MM-dd", "yyyy/MM/dd", "MM/dd/yyyy", "M/d/yyyy", "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ"];

    private readonly IEmployeeRepository _repo;
    private readonly IRoleRepository _roles;
    private readonly IHostEnvironment _env;
    private readonly ILogger<EmployeeService> _logger;

    public EmployeeService(
        IEmployeeRepository repo,
        IRoleRepository roles,
        IHostEnvironment env,
        ILogger<EmployeeService> logger)
    {
        _repo = repo;
        _roles = roles;
        _env = env;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // Permissions
    // -------------------------------------------------------------------------

    private async Task<IReadOnlyList<string>> RoleCodesAsync(HrActor actor) =>
        await _roles.ListRoleCodesByNexaUserAsync(actor.OrganizationId, actor.UserId);

    private static void Require(IReadOnlyList<string> roleCodes, string capability)
    {
        if (!HrPermissions.Has(roleCodes, capability))
            throw new HrAccessDeniedException(capability);
    }

    private async Task<IReadOnlyList<string>> RequireAsync(HrActor actor, string capability)
    {
        var codes = await RoleCodesAsync(actor);
        Require(codes, capability);
        return codes;
    }

    public async Task<IReadOnlyList<string>> GetCapabilitiesAsync(HrActor actor, CancellationToken cancellationToken = default)
    {
        var codes = await RoleCodesAsync(actor);
        return HrPermissions.ListFor(codes);
    }

    private async Task EnsureOrgReadyAsync(Guid organizationId, Guid? actorUserId, CancellationToken cancellationToken)
    {
        if (OrgInitialized.ContainsKey(organizationId)) return;

        await _repo.EnsureOrgDefaultsAsync(organizationId, cancellationToken);
        await _repo.SeedDemoEmployeesIfEmptyAsync(organizationId, actorUserId, _env.IsDevelopment(), cancellationToken);
        OrgInitialized[organizationId] = true;
    }

    // -------------------------------------------------------------------------
    // Read
    // -------------------------------------------------------------------------

    public async Task<PagedResult<EmployeeListItemDto>> ListAsync(HrActor actor, EmployeeListQuery query, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, HrPermissions.View);
        await EnsureOrgReadyAsync(actor.OrganizationId, actor.UserId, cancellationToken);

        NormalizeQuery(query);
        return await _repo.ListAsync(actor.OrganizationId, query, cancellationToken);
    }

    public async Task<EmployeeDetailDto?> GetAsync(HrActor actor, Guid employeeId, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, HrPermissions.View);
        var detail = await _repo.GetByIdAsync(actor.OrganizationId, employeeId, cancellationToken);
        if (detail == null) return null;
        return ApplyVisibility(detail, codes);
    }

    public async Task<EmployeeSummaryDto> GetSummaryAsync(HrActor actor, DateTime? from, DateTime? to, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, HrPermissions.View);
        await EnsureOrgReadyAsync(actor.OrganizationId, actor.UserId, cancellationToken);
        return await _repo.GetSummaryAsync(actor.OrganizationId, from, to, cancellationToken);
    }

    public async Task<IReadOnlyList<EmployeeActivityDto>> GetActivityAsync(HrActor actor, Guid employeeId, int take, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, HrPermissions.View);
        await RequireEmployeeAsync(actor.OrganizationId, employeeId, cancellationToken);
        return await _repo.ListActivityAsync(actor.OrganizationId, employeeId, take <= 0 ? 50 : take, cancellationToken);
    }

    public async Task<IReadOnlyList<EmploymentHistoryDto>> GetHistoryAsync(HrActor actor, Guid employeeId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, HrPermissions.View);
        await RequireEmployeeAsync(actor.OrganizationId, employeeId, cancellationToken);
        return await _repo.ListHistoryAsync(actor.OrganizationId, employeeId, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Create / update / status / archive
    // -------------------------------------------------------------------------

    public async Task<EmployeeDetailDto> CreateAsync(HrActor actor, CreateEmployeeRequest request, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, HrPermissions.Create);
        await EnsureOrgReadyAsync(actor.OrganizationId, actor.UserId, cancellationToken);

        if (!HrPermissions.Has(codes, HrPermissions.ViewSensitiveData))
            ClearSensitive(request);

        await ValidateLinkedCandidateAsync(actor, request, cancellationToken);

        var (id, _) = await CreateCoreAsync(actor, request, "Employee record created", cancellationToken);
        var detail = await _repo.GetByIdAsync(actor.OrganizationId, id, cancellationToken)
                     ?? throw EmployeeServiceException.NotFound("Employee was created but could not be loaded.");
        return ApplyVisibility(detail, codes);
    }

    public async Task<EmployeeDetailDto?> UpdateAsync(HrActor actor, Guid employeeId, UpdateEmployeeRequest request, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, HrPermissions.Edit);
        var canSensitive = HrPermissions.Has(codes, HrPermissions.ViewSensitiveData);

        var existing = await _repo.GetByIdAsync(actor.OrganizationId, employeeId, cancellationToken);
        if (existing == null) return null;
        if (existing.IsArchived)
            throw EmployeeServiceException.Conflict("Archived employees cannot be edited. Restore the employee first.", "employee_archived");

        Normalize(request);
        var errors = EmployeeValidation.ValidateEmployee(request);
        if (errors.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", errors));

        // Callers without sensitive access never see these fields, so never overwrite them.
        if (!canSensitive)
            CopySensitiveFrom(existing, request);

        // Link fields are immutable after creation.
        request.CandidateId = existing.CandidateId;
        request.ApplicationId = existing.ApplicationId;

        if (!string.IsNullOrWhiteSpace(request.EmployeeNumber)
            && !request.EmployeeNumber.Equals(existing.EmployeeNumber, StringComparison.OrdinalIgnoreCase)
            && await _repo.EmployeeNumberExistsAsync(actor.OrganizationId, request.EmployeeNumber, employeeId, cancellationToken))
        {
            throw EmployeeServiceException.Conflict($"Employee number '{request.EmployeeNumber}' is already in use.", "employee_number_exists");
        }

        await ValidateReferencesAsync(actor.OrganizationId, employeeId, request, cancellationToken);

        // Status transitions go through the status flow so termination data stays consistent.
        var requestedStatus = EmployeeValidation.NormalizeStatus(request.EmploymentStatus);
        request.EmploymentStatus = existing.EmploymentStatus;

        bool updated;
        try
        {
            updated = await _repo.UpdateAsync(actor.OrganizationId, employeeId, request, actor.UserId, cancellationToken);
        }
        catch (SqlException ex) when (IsUniqueViolation(ex))
        {
            throw EmployeeServiceException.Conflict("Employee number is already in use.", "employee_number_exists");
        }
        if (!updated) return null;

        if (request.EmergencyContacts != null)
            await _repo.ReplaceEmergencyContactsAsync(actor.OrganizationId, employeeId, request.EmergencyContacts, cancellationToken);

        var changes = DescribeChanges(existing, request);
        if (changes.Count > 0)
        {
            var snapshot = ToSnapshot(existing);
            snapshot.JobTitleId = request.JobTitleId;
            snapshot.DepartmentId = request.DepartmentId;
            snapshot.ManagerId = request.ManagerId;
            snapshot.EmploymentTypeId = request.EmploymentTypeId;
            snapshot.WorkLocationId = request.WorkLocationId;
            await _repo.AddEmploymentHistoryAsync(actor.OrganizationId, employeeId, "Update", snapshot, actor.UserId,
                "Changed: " + string.Join(", ", changes), cancellationToken);
        }

        await _repo.AddActivityAsync(actor.OrganizationId, employeeId, "Updated", "Employee record updated",
            changes.Count > 0 ? JsonSerializer.Serialize(new { changed = changes }) : null,
            actor.UserId, actor.Name, cancellationToken);

        if (requestedStatus != null && !requestedStatus.Equals(existing.EmploymentStatus, StringComparison.OrdinalIgnoreCase))
        {
            await ChangeStatusCoreAsync(actor, employeeId, new ChangeEmployeeStatusRequest { Status = requestedStatus }, cancellationToken);
        }

        var detail = await _repo.GetByIdAsync(actor.OrganizationId, employeeId, cancellationToken);
        return detail == null ? null : ApplyVisibility(detail, codes);
    }

    public async Task<bool> DeleteAsync(HrActor actor, Guid employeeId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, HrPermissions.Archive);

        var archived = await _repo.ArchiveAsync(actor.OrganizationId, employeeId, actor.UserId, cancellationToken);
        if (archived)
        {
            await _repo.AddActivityAsync(actor.OrganizationId, employeeId, "Archived", "Employee archived",
                null, actor.UserId, actor.Name, cancellationToken);
        }
        return archived;
    }

    public async Task<EmployeeDetailDto?> RestoreAsync(HrActor actor, Guid employeeId, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, HrPermissions.Archive);

        var restored = await _repo.RestoreAsync(actor.OrganizationId, employeeId, actor.UserId, cancellationToken);
        if (!restored) return null;

        await _repo.AddActivityAsync(actor.OrganizationId, employeeId, "Restored", "Employee restored from archive",
            null, actor.UserId, actor.Name, cancellationToken);

        var detail = await _repo.GetByIdAsync(actor.OrganizationId, employeeId, cancellationToken);
        return detail == null ? null : ApplyVisibility(detail, codes);
    }

    public async Task<EmployeeDetailDto?> ChangeStatusAsync(HrActor actor, Guid employeeId, ChangeEmployeeStatusRequest request, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, HrPermissions.Edit);

        if (!await ChangeStatusCoreAsync(actor, employeeId, request, cancellationToken)) return null;

        var detail = await _repo.GetByIdAsync(actor.OrganizationId, employeeId, cancellationToken);
        return detail == null ? null : ApplyVisibility(detail, codes);
    }

    private async Task<bool> ChangeStatusCoreAsync(HrActor actor, Guid employeeId, ChangeEmployeeStatusRequest request, CancellationToken cancellationToken)
    {
        var status = EmployeeValidation.NormalizeStatus(request.Status)
                     ?? throw EmployeeServiceException.Validation(
                         $"Employment status must be one of: {string.Join(", ", EmploymentStatuses.All.OrderBy(s => s))}.");

        var existing = await _repo.GetByIdAsync(actor.OrganizationId, employeeId, cancellationToken);
        if (existing == null) return false;
        if (existing.IsArchived)
            throw EmployeeServiceException.Conflict("Archived employees cannot change status. Restore the employee first.", "employee_archived");

        if (status == EmploymentStatuses.Terminated)
        {
            request.TerminationDate ??= DateTime.UtcNow.Date;
            if (existing.HireDate.HasValue && request.TerminationDate.Value.Date < existing.HireDate.Value.Date)
                throw EmployeeServiceException.Validation("Termination date cannot be before the hire date.");
        }

        request.Status = status;
        var changed = await _repo.ChangeStatusAsync(actor.OrganizationId, employeeId, request, actor.UserId, cancellationToken);
        if (!changed) return false;

        var snapshot = ToSnapshot(existing);
        snapshot.EmploymentStatus = status;
        await _repo.AddEmploymentHistoryAsync(actor.OrganizationId, employeeId, "StatusChange", snapshot, actor.UserId,
            string.IsNullOrWhiteSpace(request.Notes) ? $"{existing.EmploymentStatus} -> {status}" : request.Notes, cancellationToken);
        await _repo.AddActivityAsync(actor.OrganizationId, employeeId, "StatusChanged",
            $"Status changed from {existing.EmploymentStatus} to {status}",
            JsonSerializer.Serialize(new { from = existing.EmploymentStatus, to = status, request.TerminationReason }),
            actor.UserId, actor.Name, cancellationToken);
        return true;
    }

    // -------------------------------------------------------------------------
    // Documents
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<EmployeeDocumentDto>> ListDocumentsAsync(HrActor actor, Guid employeeId, bool includeArchived, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, HrPermissions.View);
        await RequireEmployeeAsync(actor.OrganizationId, employeeId, cancellationToken);
        return await _repo.ListDocumentsAsync(actor.OrganizationId, employeeId, includeArchived, cancellationToken);
    }

    public async Task<EmployeeDocumentDto> AddDocumentAsync(HrActor actor, Guid employeeId, UpsertEmployeeDocumentRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, HrPermissions.ManageDocuments);
        await RequireEmployeeAsync(actor.OrganizationId, employeeId, cancellationToken);

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.Category)) errors.Add("Category is required.");
        if (string.IsNullOrWhiteSpace(request.Name)) errors.Add("Name is required.");
        if (request.DocumentId == null && string.IsNullOrWhiteSpace(request.ExternalUrl))
            errors.Add("Provide a documentId or an externalUrl.");
        if (!string.IsNullOrWhiteSpace(request.ExternalUrl)
            && !(Uri.TryCreate(request.ExternalUrl.Trim(), UriKind.Absolute, out var uri)
                 && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
            errors.Add("External URL must be an http(s) URL.");

        var status = EmployeeValidation.NormalizeFromSet(EmployeeValidation.DocumentStatuses, request.Status);
        if (!string.IsNullOrWhiteSpace(request.Status) && status == null)
            errors.Add("Status must be Active, Expired, Archived or Pending.");
        if (errors.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", errors));

        request.Status = status ?? "Active";
        request.Category = request.Category.Trim();
        request.Name = request.Name.Trim();

        var rowId = await _repo.InsertDocumentAsync(actor.OrganizationId, employeeId, request, actor.UserId, cancellationToken);
        await _repo.AddActivityAsync(actor.OrganizationId, employeeId, "DocumentAdded",
            $"Document added: {request.Name}", null, actor.UserId, actor.Name, cancellationToken);

        return await _repo.GetDocumentAsync(actor.OrganizationId, rowId, cancellationToken)
               ?? throw EmployeeServiceException.NotFound("Document was created but could not be loaded.");
    }

    public async Task<bool> ArchiveDocumentAsync(HrActor actor, Guid employeeId, Guid documentRowId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, HrPermissions.ManageDocuments);

        var doc = await _repo.GetDocumentAsync(actor.OrganizationId, documentRowId, cancellationToken);
        if (doc == null || doc.EmployeeId != employeeId) return false;

        var archived = await _repo.ArchiveDocumentAsync(actor.OrganizationId, documentRowId, actor.UserId, cancellationToken);
        if (archived)
        {
            await _repo.AddActivityAsync(actor.OrganizationId, employeeId, "DocumentArchived",
                $"Document archived: {doc.Name}", null, actor.UserId, actor.Name, cancellationToken);
        }
        return archived;
    }

    // -------------------------------------------------------------------------
    // Compensation
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<CompensationDto>> ListCompensationAsync(HrActor actor, Guid employeeId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, HrPermissions.ViewCompensation);
        await RequireEmployeeAsync(actor.OrganizationId, employeeId, cancellationToken);
        return await _repo.ListCompensationAsync(actor.OrganizationId, employeeId, cancellationToken);
    }

    public async Task<CompensationDto> AddCompensationAsync(HrActor actor, Guid employeeId, UpsertCompensationRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, HrPermissions.ManageCompensation);
        await RequireEmployeeAsync(actor.OrganizationId, employeeId, cancellationToken);

        var errors = new List<string>();
        if (request.Amount < 0) errors.Add("Amount cannot be negative.");
        var currency = (request.Currency ?? string.Empty).Trim().ToUpperInvariant();
        if (currency.Length != 3 || !currency.All(char.IsLetter)) errors.Add("Currency must be a 3-letter code.");
        var frequency = EmployeeValidation.NormalizeFromSet(EmployeeValidation.PayFrequencies, request.PayFrequency);
        if (frequency == null) errors.Add("Pay frequency must be Hourly, Weekly, BiWeekly, SemiMonthly, Monthly or Annual.");
        if (request.EffectiveDate == default) errors.Add("Effective date is required.");
        if (request.EndDate.HasValue && request.EndDate.Value.Date < request.EffectiveDate.Date)
            errors.Add("End date cannot be before the effective date.");
        if (errors.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", errors));

        request.Currency = currency;
        request.PayFrequency = frequency!;

        var id = await _repo.InsertCompensationAsync(actor.OrganizationId, employeeId, request, actor.UserId, cancellationToken);
        // Never put amounts in the activity feed (visible to users without compensation access).
        await _repo.AddActivityAsync(actor.OrganizationId, employeeId, "CompensationChanged",
            "Compensation record added", null, actor.UserId, actor.Name, cancellationToken);

        var all = await _repo.ListCompensationAsync(actor.OrganizationId, employeeId, cancellationToken);
        return all.First(c => c.Id == id);
    }

    // -------------------------------------------------------------------------
    // Import / export
    // -------------------------------------------------------------------------

    public async Task<EmployeeImportResultDto> ImportAsync(HrActor actor, string csv, CancellationToken cancellationToken = default)
    {
        var codes = await RoleCodesAsync(actor);
        Require(codes, HrPermissions.Import);
        Require(codes, HrPermissions.Create);
        await EnsureOrgReadyAsync(actor.OrganizationId, actor.UserId, cancellationToken);

        var rows = EmployeeCsv.Parse(csv ?? string.Empty);
        if (rows.Count == 0)
            throw EmployeeServiceException.Validation("The CSV is empty. Include a header row and at least one data row.");

        var header = rows[0].Select(h => EmployeeCsv.NormalizeHeader(h)).ToList();
        int Col(string name) => header.IndexOf(EmployeeCsv.NormalizeHeader(name));
        var cols = ImportHeaders.ToDictionary(h => h, Col);

        if (cols["FirstName"] < 0 || cols["LastName"] < 0)
            throw EmployeeServiceException.Validation("The CSV header must include FirstName and LastName. Supported columns: " + string.Join(", ", ImportHeaders) + ".");

        var dataRows = rows.Skip(1).ToList();
        if (dataRows.Count > ImportRowCap)
            throw EmployeeServiceException.Validation($"A single import is limited to {ImportRowCap} rows.");

        var departments = ToNameMap(await _repo.ListDepartmentsAsync(actor.OrganizationId, cancellationToken));
        var titles = ToNameMap(await _repo.ListJobTitlesAsync(actor.OrganizationId, cancellationToken));
        var locations = ToNameMap((await _repo.ListLocationsAsync(actor.OrganizationId, cancellationToken)).Cast<HrLookupDto>().ToList());
        var types = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in await _repo.ListEmploymentTypesAsync(actor.OrganizationId, cancellationToken))
        {
            types[EmployeeCsv.NormalizeHeader(t.Name)] = t.Id;
            if (!string.IsNullOrWhiteSpace(t.Code)) types[EmployeeCsv.NormalizeHeader(t.Code)] = t.Id;
        }

        var errors = new List<EmployeeImportRowError>();
        var created = 0;
        var skipped = 0;
        var usedNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < dataRows.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowNumber = i + 2; // 1-based, header is row 1
            var row = dataRows[i];

            string Cell(string name)
            {
                var idx = cols[name];
                return idx >= 0 && idx < row.Count ? row[idx].Trim() : string.Empty;
            }

            try
            {
                var request = new CreateEmployeeRequest
                {
                    FirstName = Cell("FirstName"),
                    LastName = Cell("LastName"),
                    WorkEmail = NullIfEmpty(Cell("WorkEmail")),
                    EmployeeNumber = NullIfEmpty(Cell("EmployeeNumber"))
                };

                var statusText = Cell("EmploymentStatus");
                if (statusText.Length > 0)
                {
                    var status = EmployeeValidation.NormalizeStatus(statusText);
                    if (status == null) throw EmployeeServiceException.Validation($"Unknown employment status '{statusText}'.");
                    request.EmploymentStatus = status;
                }
                else
                {
                    request.EmploymentStatus = EmploymentStatuses.Active;
                }

                var hireText = Cell("HireDate");
                if (hireText.Length > 0)
                {
                    if (!TryParseDate(hireText, out var hire))
                        throw EmployeeServiceException.Validation($"Invalid hire date '{hireText}'. Use yyyy-MM-dd.");
                    request.HireDate = hire;
                }

                var typeText = Cell("EmploymentType");
                if (typeText.Length > 0)
                {
                    if (!types.TryGetValue(EmployeeCsv.NormalizeHeader(typeText), out var typeId))
                        throw EmployeeServiceException.Validation($"Unknown employment type '{typeText}'.");
                    request.EmploymentTypeId = typeId;
                }

                var errs = EmployeeValidation.ValidateEmployee(request);
                if (errs.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", errs));

                if (request.EmployeeNumber != null
                    && (!usedNumbers.Add(request.EmployeeNumber)
                        || await _repo.EmployeeNumberExistsAsync(actor.OrganizationId, request.EmployeeNumber, null, cancellationToken)))
                {
                    skipped++;
                    errors.Add(new EmployeeImportRowError { RowNumber = rowNumber, Message = $"Employee number '{request.EmployeeNumber}' already exists; row skipped." });
                    continue;
                }

                // Resolve (and create when missing) lookups only once the row itself is valid.
                request.DepartmentId = await ResolveLookupAsync(departments, Cell("Department"),
                    name => _repo.UpsertDepartmentAsync(actor.OrganizationId, new CreateHrLookupRequest { Name = name }, actor.UserId, cancellationToken));
                request.JobTitleId = await ResolveLookupAsync(titles, Cell("JobTitle"),
                    name => _repo.UpsertJobTitleAsync(actor.OrganizationId, new CreateHrLookupRequest { Name = name }, actor.UserId, cancellationToken));
                request.WorkLocationId = await ResolveLookupAsync(locations, Cell("Location"),
                    name => _repo.UpsertLocationAsync(actor.OrganizationId, new CreateHrLookupRequest { Name = name }, actor.UserId, cancellationToken));

                var (_, wasCreated) = await CreateCoreAsync(actor, request, "Employee imported from CSV", cancellationToken);
                if (wasCreated) created++; else skipped++;
            }
            catch (EmployeeServiceException ex)
            {
                skipped++;
                errors.Add(new EmployeeImportRowError { RowNumber = rowNumber, Message = ex.Message });
            }
            catch (SqlException ex)
            {
                _logger.LogWarning(ex, "Employee import row {Row} failed for organization {OrganizationId}", rowNumber, actor.OrganizationId);
                skipped++;
                errors.Add(new EmployeeImportRowError { RowNumber = rowNumber, Message = "Database error while saving this row." });
            }
        }

        await _repo.EnsureOrgDefaultsAsync(actor.OrganizationId, cancellationToken);

        return new EmployeeImportResultDto
        {
            TotalRows = dataRows.Count,
            Created = created,
            Skipped = skipped,
            Errors = errors
        };
    }

    public async Task<string> ExportAsync(HrActor actor, EmployeeListQuery query, CancellationToken cancellationToken = default)
    {
        var codes = await RoleCodesAsync(actor);
        Require(codes, HrPermissions.Export);
        var includeSensitive = HrPermissions.Has(codes, HrPermissions.ViewSensitiveData);
        var includeCompensation = includeSensitive && HrPermissions.Has(codes, HrPermissions.ViewCompensation);

        NormalizeQuery(query);
        query.PageSize = ExportPageSize;

        var items = new List<EmployeeListItemDto>();
        var page = 1;
        while (items.Count < ExportRowCap)
        {
            query.Page = page++;
            var result = await _repo.ListAsync(actor.OrganizationId, query, cancellationToken);
            if (result.Items.Count == 0) break;
            items.AddRange(result.Items);
            if (items.Count >= result.Total) break;
        }
        if (items.Count > ExportRowCap) items = items.Take(ExportRowCap).ToList();

        var headers = new List<string>
        {
            "EmployeeNumber", "FirstName", "LastName", "PreferredName", "WorkEmail", "Department", "JobTitle",
            "Location", "EmploymentStatus", "EmploymentType", "Manager", "HireDate"
        };
        if (includeSensitive) headers.AddRange(["DateOfBirth", "PersonalEmail", "PersonalPhone"]);
        if (includeCompensation) headers.AddRange(["Amount", "Currency", "PayFrequency"]);

        var details = new Dictionary<Guid, EmployeeDetailDto>();
        var compensation = new Dictionary<Guid, CompensationDto?>();
        if (includeSensitive)
        {
            foreach (var batch in items.Chunk(20))
            {
                var loaded = await Task.WhenAll(batch.Select(async item =>
                {
                    var detail = await _repo.GetByIdAsync(actor.OrganizationId, item.Id, cancellationToken);
                    CompensationDto? comp = null;
                    if (includeCompensation)
                        comp = (await _repo.ListCompensationAsync(actor.OrganizationId, item.Id, cancellationToken)).FirstOrDefault();
                    return (item.Id, detail, comp);
                }));
                foreach (var (id, detail, comp) in loaded)
                {
                    if (detail != null) details[id] = detail;
                    compensation[id] = comp;
                }
            }
        }

        var sb = new StringBuilder();
        sb.Append(EmployeeCsv.Line(headers)).Append("\r\n");
        foreach (var e in items)
        {
            var cells = new List<string?>
            {
                e.EmployeeNumber, e.FirstName, e.LastName, e.PreferredName, e.WorkEmail, e.Department, e.JobTitle,
                e.Location, e.EmploymentStatus, e.EmploymentType, e.ManagerName, FormatDate(e.HireDate)
            };
            if (includeSensitive)
            {
                details.TryGetValue(e.Id, out var d);
                cells.AddRange([FormatDate(d?.DateOfBirth), d?.PersonalEmail, d?.PersonalPhone]);
            }
            if (includeCompensation)
            {
                compensation.TryGetValue(e.Id, out var c);
                cells.AddRange([
                    c?.Amount.ToString("0.00", CultureInfo.InvariantCulture), c?.Currency, c?.PayFrequency
                ]);
            }
            sb.Append(EmployeeCsv.Line(cells)).Append("\r\n");
        }

        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // Convert from candidate
    // -------------------------------------------------------------------------

    // NOTE: converting a candidate does NOT start an onboarding. IOnboardingService depends on employee data, so
    // calling it from here would invite a circular dependency. The frontend creates the onboarding right after a
    // successful convert via the idempotent POST api/onboardings/from-employee/{employeeId}.

    public async Task<EmployeeDetailDto> ConvertFromCandidateAsync(HrActor actor, ConvertCandidateToEmployeeRequest request, CancellationToken cancellationToken = default)
    {
        var codes = await RequireAsync(actor, HrPermissions.Create);
        await EnsureOrgReadyAsync(actor.OrganizationId, actor.UserId, cancellationToken);

        if (request.CandidateId == Guid.Empty)
            throw EmployeeServiceException.Validation("candidateId is required.");

        // Idempotent: an active employee already exists for this candidate.
        var existingId = await _repo.FindActiveByCandidateIdAsync(actor.OrganizationId, request.CandidateId, cancellationToken);
        if (existingId.HasValue)
            return await LoadVisibleAsync(actor, existingId.Value, codes, cancellationToken);

        var candidate = await _repo.GetCandidateBasicsAsync(actor.OrganizationId, request.CandidateId, cancellationToken)
                        ?? throw EmployeeServiceException.NotFound("Candidate not found.");

        Guid applicationId;
        if (request.ApplicationId.HasValue)
        {
            var info = await _repo.GetApplicationHireInfoAsync(actor.OrganizationId, request.ApplicationId.Value, cancellationToken)
                       ?? throw EmployeeServiceException.NotFound("Application not found.");
            if (info.CandidateId != request.CandidateId)
                throw EmployeeServiceException.Validation("The application does not belong to this candidate.");
            if (!EmployeeValidation.IsHired(info.StageName, info.Status))
                throw EmployeeServiceException.Conflict("The application has not reached the Hired stage.", "application_not_hired");
            applicationId = info.ApplicationId;
        }
        else
        {
            var hired = await _repo.FindHiredApplicationForCandidateAsync(actor.OrganizationId, request.CandidateId, cancellationToken)
                        ?? throw EmployeeServiceException.Conflict("The candidate has no hired application.", "application_not_hired");
            applicationId = hired.ApplicationId;
        }

        var employee = request.Overrides ?? new CreateEmployeeRequest();
        employee.FirstName = FirstNonEmpty(employee.FirstName, candidate.FirstName) ?? string.Empty;
        employee.LastName = FirstNonEmpty(employee.LastName, candidate.LastName) ?? string.Empty;
        employee.PersonalEmail = FirstNonEmpty(employee.PersonalEmail, candidate.Email);
        employee.PersonalPhone = FirstNonEmpty(employee.PersonalPhone, candidate.Phone);
        employee.CandidateId = request.CandidateId;
        employee.ApplicationId = applicationId;
        employee.HireDate ??= DateTime.UtcNow.Date;
        if (string.IsNullOrWhiteSpace(employee.EmploymentStatus))
            employee.EmploymentStatus = EmploymentStatuses.Onboarding;

        var (id, _) = await CreateCoreAsync(actor, employee, "Employee created from hired candidate", cancellationToken);
        return await LoadVisibleAsync(actor, id, codes, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Lookups
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<HrLookupDto>> ListDepartmentsAsync(HrActor actor, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, HrPermissions.View);
        await EnsureOrgReadyAsync(actor.OrganizationId, actor.UserId, cancellationToken);
        return await _repo.ListDepartmentsAsync(actor.OrganizationId, cancellationToken);
    }

    public async Task<IReadOnlyList<HrLookupDto>> ListJobTitlesAsync(HrActor actor, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, HrPermissions.View);
        await EnsureOrgReadyAsync(actor.OrganizationId, actor.UserId, cancellationToken);
        return await _repo.ListJobTitlesAsync(actor.OrganizationId, cancellationToken);
    }

    public async Task<IReadOnlyList<WorkLocationDto>> ListLocationsAsync(HrActor actor, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, HrPermissions.View);
        await EnsureOrgReadyAsync(actor.OrganizationId, actor.UserId, cancellationToken);
        return await _repo.ListLocationsAsync(actor.OrganizationId, cancellationToken);
    }

    public async Task<IReadOnlyList<HrLookupDto>> ListEmploymentTypesAsync(HrActor actor, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, HrPermissions.View);
        await EnsureOrgReadyAsync(actor.OrganizationId, actor.UserId, cancellationToken);
        return await _repo.ListEmploymentTypesAsync(actor.OrganizationId, cancellationToken);
    }

    public async Task<HrLookupDto> UpsertDepartmentAsync(HrActor actor, CreateHrLookupRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, HrPermissions.Edit);
        RequireLookupName(request);
        var id = await _repo.UpsertDepartmentAsync(actor.OrganizationId, request, actor.UserId, cancellationToken);
        return (await _repo.ListDepartmentsAsync(actor.OrganizationId, cancellationToken)).First(x => x.Id == id);
    }

    public async Task<HrLookupDto> UpsertJobTitleAsync(HrActor actor, CreateHrLookupRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, HrPermissions.Edit);
        RequireLookupName(request);
        var id = await _repo.UpsertJobTitleAsync(actor.OrganizationId, request, actor.UserId, cancellationToken);
        return (await _repo.ListJobTitlesAsync(actor.OrganizationId, cancellationToken)).First(x => x.Id == id);
    }

    public async Task<WorkLocationDto> UpsertLocationAsync(HrActor actor, CreateHrLookupRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(actor, HrPermissions.Edit);
        RequireLookupName(request);
        var id = await _repo.UpsertLocationAsync(actor.OrganizationId, request, actor.UserId, cancellationToken);
        return (await _repo.ListLocationsAsync(actor.OrganizationId, cancellationToken)).First(x => x.Id == id);
    }

    // -------------------------------------------------------------------------
    // Shared create path
    // -------------------------------------------------------------------------

    /// <summary>
    /// Validates and inserts an employee plus emergency contacts, history and activity rows.
    /// Repository calls use independent connections, so atomicity is achieved by compensation:
    /// if any follow-up write fails, the new employee is archived (freeing the candidate/number
    /// uniqueness for a retry of convert) and the error is rethrown.
    /// Returns (id, created); created is false when a concurrent convert already created the employee.
    /// </summary>
    private async Task<(Guid Id, bool Created)> CreateCoreAsync(
        HrActor actor,
        CreateEmployeeRequest request,
        string activitySummary,
        CancellationToken cancellationToken)
    {
        Normalize(request);
        var errors = EmployeeValidation.ValidateEmployee(request);
        if (errors.Count > 0) throw EmployeeServiceException.Validation(string.Join(" ", errors));

        var employeeId = Guid.NewGuid();
        await ValidateReferencesAsync(actor.OrganizationId, employeeId, request, cancellationToken);

        request.EmploymentStatus = EmployeeValidation.NormalizeStatus(request.EmploymentStatus) ?? EmploymentStatuses.Pending;

        var providedNumber = string.IsNullOrWhiteSpace(request.EmployeeNumber) ? null : request.EmployeeNumber.Trim();
        if (providedNumber != null
            && await _repo.EmployeeNumberExistsAsync(actor.OrganizationId, providedNumber, null, cancellationToken))
        {
            throw EmployeeServiceException.Conflict($"Employee number '{providedNumber}' is already in use.", "employee_number_exists");
        }

        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            var number = providedNumber ?? await _repo.NextEmployeeNumberAsync(actor.OrganizationId, cancellationToken);
            try
            {
                await _repo.InsertAsync(actor.OrganizationId, employeeId, request, number, actor.UserId, cancellationToken);
                break;
            }
            catch (SqlException ex) when (IsUniqueViolation(ex))
            {
                if (request.CandidateId.HasValue)
                {
                    var concurrent = await _repo.FindActiveByCandidateIdAsync(actor.OrganizationId, request.CandidateId.Value, cancellationToken);
                    if (concurrent.HasValue) return (concurrent.Value, false);
                }
                if (providedNumber != null)
                    throw EmployeeServiceException.Conflict($"Employee number '{providedNumber}' is already in use.", "employee_number_exists");
                if (attempt >= maxAttempts)
                    throw EmployeeServiceException.Conflict("Could not allocate a unique employee number. Please retry.", "employee_number_exists");
            }
        }

        try
        {
            if (request.EmergencyContacts is { Count: > 0 })
                await _repo.ReplaceEmergencyContactsAsync(actor.OrganizationId, employeeId, request.EmergencyContacts, cancellationToken);

            await _repo.AddEmploymentHistoryAsync(actor.OrganizationId, employeeId, "Hire", request, actor.UserId, null, cancellationToken);
            await _repo.AddActivityAsync(actor.OrganizationId, employeeId, "Created", activitySummary,
                request.CandidateId.HasValue ? JsonSerializer.Serialize(new { candidateId = request.CandidateId, applicationId = request.ApplicationId }) : null,
                actor.UserId, actor.Name, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Employee {EmployeeId} follow-up writes failed; archiving to compensate.", employeeId);
            try
            {
                await _repo.ArchiveAsync(actor.OrganizationId, employeeId, actor.UserId, CancellationToken.None);
            }
            catch (Exception archiveEx)
            {
                _logger.LogError(archiveEx, "Failed to archive partially created employee {EmployeeId}.", employeeId);
            }
            throw;
        }

        return (employeeId, true);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static bool IsUniqueViolation(SqlException ex) => ex.Number is 2601 or 2627;

    private async Task RequireEmployeeAsync(Guid organizationId, Guid employeeId, CancellationToken cancellationToken)
    {
        var employee = await _repo.GetByIdAsync(organizationId, employeeId, cancellationToken);
        if (employee == null) throw EmployeeServiceException.NotFound("Employee not found.");
    }

    private async Task<EmployeeDetailDto> LoadVisibleAsync(HrActor actor, Guid employeeId, IReadOnlyList<string> codes, CancellationToken cancellationToken)
    {
        var detail = await _repo.GetByIdAsync(actor.OrganizationId, employeeId, cancellationToken)
                     ?? throw EmployeeServiceException.NotFound("Employee not found.");
        return ApplyVisibility(detail, codes);
    }

    /// <summary>
    /// Nulls fields the caller may not see. Compensation is never part of the detail payload
    /// (it is only served by the dedicated compensation endpoints, gated by ViewCompensation).
    /// </summary>
    private static EmployeeDetailDto ApplyVisibility(EmployeeDetailDto detail, IReadOnlyList<string> codes)
    {
        if (!HrPermissions.Has(codes, HrPermissions.ViewSensitiveData))
        {
            detail.DateOfBirth = null;
            detail.PersonalEmail = null;
            detail.PersonalPhone = null;
            detail.AddressLine1 = null;
            detail.AddressLine2 = null;
            detail.City = null;
            detail.State = null;
            detail.Country = null;
            detail.PostalCode = null;
        }
        return detail;
    }

    private static void ClearSensitive(CreateEmployeeRequest r)
    {
        r.DateOfBirth = null;
        r.PersonalEmail = null;
        r.PersonalPhone = null;
        r.AddressLine1 = null;
        r.AddressLine2 = null;
        r.City = null;
        r.State = null;
        r.Country = null;
        r.PostalCode = null;
    }

    private static void CopySensitiveFrom(EmployeeDetailDto source, CreateEmployeeRequest target)
    {
        target.DateOfBirth = source.DateOfBirth;
        target.PersonalEmail = source.PersonalEmail;
        target.PersonalPhone = source.PersonalPhone;
        target.AddressLine1 = source.AddressLine1;
        target.AddressLine2 = source.AddressLine2;
        target.City = source.City;
        target.State = source.State;
        target.Country = source.Country;
        target.PostalCode = source.PostalCode;
    }

    private static CreateEmployeeRequest ToSnapshot(EmployeeDetailDto d) => new()
    {
        FirstName = d.FirstName,
        LastName = d.LastName,
        JobTitleId = d.JobTitleId,
        DepartmentId = d.DepartmentId,
        ManagerId = d.ManagerId,
        EmploymentTypeId = d.EmploymentTypeId,
        EmploymentStatus = d.EmploymentStatus,
        WorkLocationId = d.WorkLocationId
    };

    private static List<string> DescribeChanges(EmployeeDetailDto before, CreateEmployeeRequest after)
    {
        var changes = new List<string>();
        if (before.JobTitleId != after.JobTitleId) changes.Add("job title");
        if (before.DepartmentId != after.DepartmentId) changes.Add("department");
        if (before.ManagerId != after.ManagerId) changes.Add("manager");
        if (before.EmploymentTypeId != after.EmploymentTypeId) changes.Add("employment type");
        if (before.WorkLocationId != after.WorkLocationId) changes.Add("work location");
        return changes;
    }

    private static void Normalize(CreateEmployeeRequest r)
    {
        r.FirstName = (r.FirstName ?? string.Empty).Trim();
        r.LastName = (r.LastName ?? string.Empty).Trim();
        r.EmployeeNumber = NullIfEmpty(r.EmployeeNumber);
        r.WorkEmail = NullIfEmpty(r.WorkEmail);
        r.PersonalEmail = NullIfEmpty(r.PersonalEmail);
        r.WorkModality = EmployeeValidation.NormalizeFromSet(EmployeeValidation.WorkModalities, r.WorkModality) ?? NullIfEmpty(r.WorkModality);
    }

    private static void NormalizeQuery(EmployeeListQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.EmploymentStatus))
        {
            query.EmploymentStatus = EmployeeValidation.NormalizeStatus(query.EmploymentStatus)
                                     ?? throw EmployeeServiceException.Validation(
                                         $"Employment status filter must be one of: {string.Join(", ", EmploymentStatuses.All.OrderBy(s => s))}.");
        }
        else
        {
            query.EmploymentStatus = null;
        }
        query.Page = Math.Max(1, query.Page);
        query.PageSize = query.PageSize <= 0 ? 25 : Math.Min(query.PageSize, 200);
        query.Dir = string.Equals(query.Dir, "desc", StringComparison.OrdinalIgnoreCase) ? "desc" : "asc";
        query.Sort = string.IsNullOrWhiteSpace(query.Sort) ? "name" : query.Sort.Trim();
    }

    /// <summary>
    /// Ensures lookup ids belong to the organization (prevents cross-tenant FK references)
    /// and the proposed manager neither is the employee nor creates a reporting cycle.
    /// </summary>
    private async Task ValidateReferencesAsync(Guid organizationId, Guid employeeId, CreateEmployeeRequest r, CancellationToken cancellationToken)
    {
        if (r.DepartmentId.HasValue
            && (await _repo.ListDepartmentsAsync(organizationId, cancellationToken)).All(x => x.Id != r.DepartmentId.Value))
            throw EmployeeServiceException.Validation("Department not found.");

        if (r.JobTitleId.HasValue
            && (await _repo.ListJobTitlesAsync(organizationId, cancellationToken)).All(x => x.Id != r.JobTitleId.Value))
            throw EmployeeServiceException.Validation("Job title not found.");

        if (r.WorkLocationId.HasValue
            && (await _repo.ListLocationsAsync(organizationId, cancellationToken)).All(x => x.Id != r.WorkLocationId.Value))
            throw EmployeeServiceException.Validation("Work location not found.");

        if (r.EmploymentTypeId.HasValue
            && (await _repo.ListEmploymentTypesAsync(organizationId, cancellationToken)).All(x => x.Id != r.EmploymentTypeId.Value))
            throw EmployeeServiceException.Validation("Employment type not found.");

        if (r.ManagerId.HasValue)
        {
            if (r.ManagerId.Value == employeeId)
                throw EmployeeServiceException.Validation("An employee cannot be their own manager.", "manager_cycle");

            var manager = await _repo.GetByIdAsync(organizationId, r.ManagerId.Value, cancellationToken);
            if (manager == null) throw EmployeeServiceException.Validation("Manager not found.");
            if (manager.IsArchived) throw EmployeeServiceException.Validation("The selected manager is archived.");

            if (await _repo.WouldCreateManagerCycleAsync(organizationId, employeeId, r.ManagerId.Value, cancellationToken))
                throw EmployeeServiceException.Validation("This manager assignment would create a reporting cycle.", "manager_cycle");
        }
    }

    private async Task ValidateLinkedCandidateAsync(HrActor actor, CreateEmployeeRequest request, CancellationToken cancellationToken)
    {
        if (request.CandidateId.HasValue)
        {
            if (await _repo.GetCandidateBasicsAsync(actor.OrganizationId, request.CandidateId.Value, cancellationToken) == null)
                throw EmployeeServiceException.NotFound("Candidate not found.");
            if (await _repo.FindActiveByCandidateIdAsync(actor.OrganizationId, request.CandidateId.Value, cancellationToken) != null)
                throw EmployeeServiceException.Conflict("An employee already exists for this candidate.", "candidate_already_converted");
        }

        if (request.ApplicationId.HasValue)
        {
            var info = await _repo.GetApplicationHireInfoAsync(actor.OrganizationId, request.ApplicationId.Value, cancellationToken)
                       ?? throw EmployeeServiceException.NotFound("Application not found.");
            if (request.CandidateId.HasValue && info.CandidateId != request.CandidateId.Value)
                throw EmployeeServiceException.Validation("The application does not belong to this candidate.");
            request.CandidateId ??= info.CandidateId;
        }
    }

    private static void RequireLookupName(CreateHrLookupRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw EmployeeServiceException.Validation("Name is required.");
        if (request.Name.Trim().Length > 200)
            throw EmployeeServiceException.Validation("Name must be 200 characters or fewer.");
    }

    private static Dictionary<string, Guid> ToNameMap(IReadOnlyList<HrLookupDto> items)
    {
        var map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items) map[item.Name.Trim()] = item.Id;
        return map;
    }

    private static async Task<Guid?> ResolveLookupAsync(Dictionary<string, Guid> cache, string name, Func<string, Task<Guid>> create)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var key = name.Trim();
        if (cache.TryGetValue(key, out var id)) return id;
        id = await create(key);
        cache[key] = id;
        return id;
    }

    private static bool TryParseDate(string text, out DateTime date)
    {
        if (DateTime.TryParseExact(text, ImportDateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out date))
        {
            date = date.Date;
            return true;
        }
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out date))
        {
            date = date.Date;
            return true;
        }
        return false;
    }

    private static string? FormatDate(DateTime? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? FirstNonEmpty(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) ? a.Trim() : (!string.IsNullOrWhiteSpace(b) ? b.Trim() : null);
}
