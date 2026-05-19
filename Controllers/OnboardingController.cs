using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs;
using nexthire_api.Services;
using nexthire_api.Exceptions;

namespace nexthire_api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class OnboardingController : ControllerBase
{
    private readonly INexaClient _nexaClient;
    private readonly ILogger<OnboardingController> _logger;

    public OnboardingController(INexaClient nexaClient, ILogger<OnboardingController> logger)
    {
        _nexaClient = nexaClient;
        _logger = logger;
    }

    /// <summary>
    /// Create organization in Nexa
    /// </summary>
    [HttpPost("organization")]
    public async Task<ActionResult<CreateOrganizationResponseDto>> CreateOrganization([FromBody] CreateOrganizationRequestDto request)
    {
        // Validate JWT claims - only check nexa_user_id to confirm user is logged in
        var userId = User.FindFirst("nexa_user_id")?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("Create organization request missing nexa_user_id claim");
            return Unauthorized(new { message = "Invalid token claims" });
        }

        // Get Nexa access token from header
        if (!Request.Headers.TryGetValue("X-Nexa-Access-Token", out var nexaTokenHeader) || string.IsNullOrEmpty(nexaTokenHeader))
        {
            _logger.LogWarning("Create organization request missing X-Nexa-Access-Token header");
            return Unauthorized(new { message = "Missing Nexa access token" });
        }

        var nexaAccessToken = nexaTokenHeader.ToString();

        // Validate and trim input
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { message = "Name is required" });
        }

        var trimmedName = request.Name.Trim();
        if (trimmedName.Length < 2 || trimmedName.Length > 120)
        {
            return BadRequest(new { message = "Name must be between 2 and 120 characters" });
        }

        // Timezone: if empty, default to "America/Costa_Rica"
        var timezone = string.IsNullOrWhiteSpace(request.Timezone) 
            ? "America/Costa_Rica" 
            : request.Timezone.Trim();

        _logger.LogInformation("Creating organization for user {UserId}. Name length: {NameLength}, Timezone: {Timezone}",
            userId, trimmedName.Length, timezone);

        try
        {
            // Call Nexa to create organization
            var nexaResponse = await _nexaClient.CreateOrgAsync(trimmedName, timezone, nexaAccessToken);

            // Validate response
            if (nexaResponse == null || string.IsNullOrEmpty(nexaResponse.OrganizationId))
            {
                _logger.LogError("Nexa returned invalid create organization response");
                return StatusCode(500, new { message = "Invalid Nexa response format" });
            }

            // Build organization object from Nexa response
            var organization = new
            {
                organizationId = nexaResponse.OrganizationId,
                name = nexaResponse.Name,
                slug = nexaResponse.Slug,
                createdAt = nexaResponse.CreatedAt,
                role = nexaResponse.Role,
                isActive = nexaResponse.IsActive
            };

            var response = new CreateOrganizationResponseDto
            {
                Organization = organization,
                Features = nexaResponse.Features,
                RequiresOrgSetup = false
            };

            _logger.LogInformation("Organization created successfully for user {UserId}", userId);
            return Ok(response);
        }
        catch (NexaAuthException ex)
        {
            // 401 from Nexa
            _logger.LogWarning(ex, "Nexa token expired for user {UserId}", userId);
            return Unauthorized(new { message = "Nexa token expired, login again" });
        }
        catch (HttpRequestException ex)
        {
            var statusCode = ex.Data.Contains("StatusCode")
                ? (System.Net.HttpStatusCode?)ex.Data["StatusCode"]
                : null;

            var errorBody = ex.Data.Contains("ErrorBody")
                ? ex.Data["ErrorBody"]?.ToString()
                : null;

            if (statusCode == System.Net.HttpStatusCode.BadRequest)
            {
                _logger.LogWarning(ex, "Bad request creating organization for user {UserId}", userId);
                // Passthrough error body
                if (!string.IsNullOrEmpty(errorBody))
                {
                    try
                    {
                        var errorJson = System.Text.Json.JsonSerializer.Deserialize<object>(errorBody);
                        return BadRequest(errorJson);
                    }
                    catch
                    {
                        return BadRequest(new { message = errorBody });
                    }
                }
                return BadRequest(new { message = "Invalid request" });
            }
            else if (statusCode == System.Net.HttpStatusCode.Conflict)
            {
                _logger.LogWarning(ex, "Organization already exists for user {UserId}", userId);
                // Passthrough error body if available
                if (!string.IsNullOrEmpty(errorBody))
                {
                    try
                    {
                        var errorJson = System.Text.Json.JsonSerializer.Deserialize<object>(errorBody);
                        return Conflict(errorJson);
                    }
                    catch
                    {
                        return Conflict(new { message = errorBody });
                    }
                }
                return Conflict(new { message = "Organization already exists" });
            }
            else
            {
                _logger.LogError(ex, "Error creating organization for user {UserId}", userId);
                return StatusCode(500, new { message = "An error occurred while creating the organization" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating organization for user {UserId}", userId);
            return StatusCode(500, new { message = "An error occurred while creating the organization" });
        }
    }
}
