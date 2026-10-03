using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.Security;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

[ApiController]
[Route("api/documents")]
[Authorize]
public class DocumentsController : ControllerBase
{
    private readonly IDocumentService _documentService;
    private readonly ILogger<DocumentsController> _logger;

    public DocumentsController(IDocumentService documentService, ILogger<DocumentsController> logger)
    {
        _documentService = documentService;
        _logger = logger;
    }

    /// <summary>
    /// Signed download URL for a document that belongs to the current organization.
    /// </summary>
    [HttpGet("{documentId}/download-url")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDownloadUrl(Guid documentId, CancellationToken ct)
    {
        try
        {
            var orgId = ClaimUtils.RequireOrgId(User);
            var url = await _documentService.GetResumeDownloadUrlAsync(orgId, documentId, ct);
            return Ok(new { downloadUrl = url });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (DocumentServiceException ex)
        {
            _logger.LogWarning(
                "Document download-url failed. DocumentId={DocumentId} Kind={Kind} UpstreamStatus={UpstreamStatus}",
                documentId,
                ex.Kind,
                ex.UpstreamStatusCode);

            return ex.Kind switch
            {
                DocumentServiceErrorKind.NotFound => NotFound(new { message = "Resume document not found." }),
                DocumentServiceErrorKind.UpstreamUnauthorized => StatusCode(StatusCodes.Status502BadGateway, new { message = "Resume service unauthorized." }),
                DocumentServiceErrorKind.Timeout => StatusCode(StatusCodes.Status504GatewayTimeout, new { message = "Resume service timeout." }),
                _ => StatusCode(500, new { message = "An error occurred while retrieving resume download URL" })
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting download-url for document {DocumentId}", documentId);
            return StatusCode(500, new { message = "An error occurred while retrieving resume download URL" });
        }
    }
}
