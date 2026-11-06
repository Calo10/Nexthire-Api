using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs;
using nexthire_api.Repositories;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

[ApiController]
[Route("api/public/candidate-links")]
[AllowAnonymous]
public class PublicCandidateLinksController : ControllerBase
{
    private readonly IPublicCandidateLinkRepository _links;
    private readonly ICandidateRepository _candidates;
    private readonly IDocumentService _documents;
    private readonly INexaClient _nexaClient;
    private readonly ILogger<PublicCandidateLinksController> _logger;

    public PublicCandidateLinksController(
        IPublicCandidateLinkRepository links,
        ICandidateRepository candidates,
        IDocumentService documents,
        INexaClient nexaClient,
        ILogger<PublicCandidateLinksController> logger)
    {
        _links = links;
        _candidates = candidates;
        _documents = documents;
        _nexaClient = nexaClient;
        _logger = logger;
    }

    [HttpGet("{linkId:guid}")]
    public async Task<ActionResult<PublicCandidateLinkListDto>> List(
        Guid linkId,
        [FromQuery] Guid orgId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        try
        {
            if (orgId == Guid.Empty)
                return BadRequest(new { message = "orgId is required" });
            if (page < 1)
                return BadRequest(new { message = "page must be >= 1" });
            if (pageSize < 1 || pageSize > 50)
                return BadRequest(new { message = "pageSize must be between 1 and 50" });

            var suspended = await SuspendedAccountResultAsync(orgId);
            if (suspended != null)
                return suspended;

            var link = await _links.GetByIdAsync(linkId);
            if (link is null || link.OrgId != orgId)
                return NotFound(new { message = "Public candidate link not found" });

            var tagIds = ReadTagIds(link.TagIdsJson);
            var result = await _candidates.GetPagedAsync(
                orgId,
                link.Search,
                link.Source,
                link.FromAt,
                link.ToAt,
                tagIds,
                page,
                pageSize,
                "created_at",
                "desc");

            return Ok(new PublicCandidateLinkListDto
            {
                Id = link.Id,
                Name = link.Name,
                Page = result.Page,
                PageSize = result.PageSize,
                Total = result.Total,
                Items = result.Items.Select(item => new PublicCandidateCardDto
                {
                    Id = item.Id,
                    DisplayName = PublicCandidateNames.Partial(item.FirstName, item.LastName),
                    Source = item.Source,
                    Tags = PublicCandidateNames.Tags(item.Tags),
                    CreatedAt = item.CreatedAt
                }).ToList()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing public candidates for link {LinkId}", linkId);
            return StatusCode(500, new { message = "An error occurred while retrieving candidates" });
        }
    }

    [HttpGet("{linkId:guid}/candidates/{candidateId:guid}")]
    public async Task<ActionResult<PublicCandidateDetailDto>> Detail(
        Guid linkId,
        Guid candidateId,
        [FromQuery] Guid orgId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (orgId == Guid.Empty)
                return BadRequest(new { message = "orgId is required" });

            var suspended = await SuspendedAccountResultAsync(orgId);
            if (suspended != null)
                return suspended;

            var link = await _links.GetByIdAsync(linkId);
            if (link is null || link.OrgId != orgId)
                return NotFound(new { message = "Public candidate link not found" });

            var tagIds = ReadTagIds(link.TagIdsJson);
            var matches = await _candidates.MatchesListFiltersAsync(
                orgId,
                candidateId,
                link.Search,
                link.Source,
                link.FromAt,
                link.ToAt,
                tagIds);
            if (!matches)
                return NotFound(new { message = "Candidate not found" });

            var candidate = await _candidates.GetByIdAsync(orgId, candidateId);
            if (candidate is null)
                return NotFound(new { message = "Candidate not found" });

            return Ok(new PublicCandidateDetailDto
            {
                Id = candidate.Id,
                DisplayName = PublicCandidateNames.Partial(candidate.FirstName, candidate.LastName),
                Source = candidate.Source,
                Tags = PublicCandidateNames.Tags(candidate.Tags),
                CreatedAt = candidate.CreatedAt,
                Analysis = await LoadAnalysisAsync(orgId, candidate.ResumeUrl, cancellationToken)
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting public candidate {CandidateId} for link {LinkId}", candidateId, linkId);
            return StatusCode(500, new { message = "An error occurred while retrieving the candidate" });
        }
    }

    private async Task<DocumentAnalysisEnvelopeDto?> LoadAnalysisAsync(Guid orgId, string? resumeUrl, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(resumeUrl, out var documentId) || documentId == Guid.Empty)
            return null;

        try
        {
            return await _documents.GetResumeAnalysisAsync(orgId, documentId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Public resume analysis unavailable for org {OrgId}", orgId);
            return null;
        }
    }

    private static IReadOnlyList<Guid> ReadTagIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<Guid>();
        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(json)?
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToArray()
                ?? Array.Empty<Guid>();
        }
        catch (JsonException)
        {
            return Array.Empty<Guid>();
        }
    }

    private async Task<ActionResult?> SuspendedAccountResultAsync(Guid orgId)
    {
        var status = await _nexaClient.GetOrganizationStatusAsync(orgId);
        if (!string.Equals(status, "suspended", StringComparison.OrdinalIgnoreCase))
            return null;

        _logger.LogInformation("Public candidate link blocked because organization {OrgId} is suspended", orgId);
        return StatusCode(StatusCodes.Status403Forbidden, new
        {
            message = "This account is suspended. Please contact the administrator.",
            code = "account_suspended"
        });
    }
}
