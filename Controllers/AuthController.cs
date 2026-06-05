using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs;
using nexthire_api.Services;
using nexthire_api.Exceptions;

namespace nexthire_api.Controllers;

[ApiController]
[Route("api")]

public class AuthController : ControllerBase
{
    private readonly INexaClient _nexaClient;
    private readonly IEmailService _emailService;
    private readonly IAuthLinkTokenStore _authLinkTokenStore;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly INexaAccessTokenResolver _nexaTokens;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        INexaClient nexaClient,
        IEmailService emailService,
        IAuthLinkTokenStore authLinkTokenStore,
        IJwtTokenService jwtTokenService,
        INexaAccessTokenResolver nexaTokens,
        ILogger<AuthController> logger)
    {
        _nexaClient = nexaClient;
        _emailService = emailService;
        _authLinkTokenStore = authLinkTokenStore;
        _jwtTokenService = jwtTokenService;
        _nexaTokens = nexaTokens;
        _logger = logger;
    }

    /// <summary>
    /// Request a magic link for authentication
    /// </summary>
    [HttpPost("auth/magic-link")]
    [AllowAnonymous]
    public async Task<IActionResult> RequestMagicLink([FromBody] MagicLinkRequestDto request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            var callbackUrl = string.IsNullOrWhiteSpace(request.CallbackUrl)
                ? null
                : request.CallbackUrl.Trim();

            // Magic links are issued and validated by Nexa (same token for /api/auth/consume).
            await _nexaClient.RequestMagicLinkAsync(request.Email, callbackUrl, cancellationToken);
            return Ok(new { message = "If the email exists, a magic link has been sent." });
        }
        catch (HttpRequestException ex)
        {
            // Server error from Nexa API (5xx) or communication error
            _logger.LogError(ex, "Error requesting magic link from Nexa API for email {Email}", request.Email);
            return StatusCode(502, new { message = "Unable to process magic link request. Please try again later." });
        }
        catch (TaskCanceledException ex)
        {
            // Timeout error
            _logger.LogError(ex, "Timeout requesting magic link from Nexa API for email {Email}", request.Email);
            return StatusCode(504, new { message = "Request timeout. Please try again later." });
        }
        catch (Exception ex)
        {
            // Unexpected error
            _logger.LogError(ex, "Unexpected error requesting magic link for email {Email}", request.Email);
            return StatusCode(500, new { message = "An error occurred while processing your request." });
        }
    }

    /// <summary>
    /// Exchange Nexa token for NextHire JWT
    /// </summary>
    [HttpPost("auth/consume")]
    [AllowAnonymous]
    public async Task<ActionResult<ExchangeTokenResponseDto>> ExchangeToken([FromBody] ExchangeTokenRequestDto? request)
    {
        // Validate request and token
        if (request == null || string.IsNullOrWhiteSpace(request.Token))
        {
            _logger.LogWarning("Exchange token request missing or token is empty");
            return BadRequest(new { message = "Missing token" });
        }

        var token = NormalizeMagicLinkToken(request.Token);

        _logger.LogInformation(
            "Processing token exchange. Token length: {TokenLength}, SegmentCount: {SegmentCount}",
            token.Length,
            token.Count(c => c == '.') + 1);

        try
        {
            var nexaResponse = await _nexaClient.ExchangeTokenAsync(token);

            // Validate Nexa response structure
            if (nexaResponse == null)
            {
                _logger.LogError("Nexa returned null response");
                return StatusCode(500, new { message = "Invalid Nexa response format" });
            }

            // User is required
            if (nexaResponse.User == null)
            {
                _logger.LogError("Nexa response missing required user field");
                return StatusCode(500, new { message = "Invalid Nexa response format" });
            }

            // Validate required user fields
            if (string.IsNullOrEmpty(nexaResponse.User.UserId) || string.IsNullOrEmpty(nexaResponse.User.Email))
            {
                _logger.LogError("Nexa user missing required fields (userId or email)");
                return StatusCode(500, new { message = "Invalid Nexa response format" });
            }

            // Log response structure for debugging
            _logger.LogInformation(
                "Nexa consume response received. RequiresOrgSetup: {RequiresOrgSetup}, HasOrganization: {HasOrganization}, HasFeatures: {HasFeatures}",
                nexaResponse.RequiresOrgSetup,
                nexaResponse.Organization != null,
                nexaResponse.Features != null);

            // Generate NextHire JWT with claims
            var accessToken = _jwtTokenService.GenerateToken(
                nexaResponse.User.UserId,
                nexaResponse.User.Email,
                nexaResponse.RequiresOrgSetup,
                nexaResponse.Organization?.GetOrgId()
            );

            // Store Nexa tokens for later use (keyed by userId)
            _logger.LogInformation("Storing Nexa tokens for user {UserId} after successful exchange. ExpiresAt: {ExpiresAt}", 
                nexaResponse.User.UserId, nexaResponse.ExpiresAt);
            await _nexaTokens.PersistTokensAsync(
                nexaResponse.User.UserId,
                nexaResponse.AccessToken,
                nexaResponse.RefreshToken,
                nexaResponse.ExpiresAt);

            var response = new ExchangeTokenResponseDto
            {
                AccessToken = accessToken,
                Nexa = new NexaTokensDto
                {
                    AccessToken = nexaResponse.AccessToken,
                    RefreshToken = nexaResponse.RefreshToken,
                    ExpiresAt = nexaResponse.ExpiresAt
                },
                RequiresOrgSetup = nexaResponse.RequiresOrgSetup,
                User = nexaResponse.User,
                Organization = nexaResponse.Organization,
                Features = nexaResponse.Features
            };

            return Ok(response);
        }
        catch (NexaAuthException ex)
        {
            if (ex.StatusCode == 400 || ex.StatusCode == 401)
            {
                _logger.LogWarning(ex, "Nexa token exchange failed with {StatusCode}: {ErrorMessage}", ex.StatusCode, ex.ErrorMessage);
                return Unauthorized(new
                {
                    message = "Invalid or expired token. Request a new magic link and open it once (email scanners can consume the link before you)."
                });
            }
            else
            {
                _logger.LogError(ex, "Nexa auth error with status {StatusCode}: {ErrorMessage}", ex.StatusCode, ex.ErrorMessage);
                return StatusCode(500, new { message = "Nexa auth error" });
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error during Nexa token exchange");
            return StatusCode(500, new { message = "An error occurred during token exchange" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error exchanging token");
            return StatusCode(500, new { message = "An error occurred during token exchange" });
        }
    }

    /// <summary>
    /// Get current authenticated user information
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    public IActionResult GetMe()
    {
        var userId = User.FindFirst("nexa_user_id")?.Value;
        var email = User.FindFirst(ClaimTypes.Email)?.Value;
        var requiresOrgSetup = User.FindFirst("requires_org_setup")?.Value;

        if (userId == null || email == null || requiresOrgSetup == null)
        {
            return Unauthorized(new { message = "Invalid token claims" });
        }

        var response = new
        {
            nexa_user_id = userId,
            email = email,
            requires_org_setup = bool.Parse(requiresOrgSetup)
        };

        return Ok(response);
    }

    /// <summary>
    /// Logout (stateless - frontend deletes token)
    /// </summary>
    [HttpPost("logout")]
    [Authorize]
    public IActionResult Logout()
    {
        // Stateless logout - frontend handles token deletion
        return NoContent();
    }

    /// <summary>
    /// Request password reset
    /// </summary>
    [HttpPost("auth/password/reset/request")]
    [AllowAnonymous]
    public async Task<IActionResult> RequestPasswordReset([FromBody] PasswordResetRequestDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return BadRequest(new { message = "Email is required" });
        }

        try
        {
            var rawToken = GenerateRawToken();
            _authLinkTokenStore.StorePasswordResetToken(rawToken, request.Email, DateTimeOffset.UtcNow.AddMinutes(30));
            await _emailService.SendPasswordResetLinkAsync(request.Email, rawToken);
            // Always return 200 OK - don't reveal if email exists
            return Ok(new { message = "If the email exists, a password reset link has been sent." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error requesting password reset for email");
            // Still return 200 OK to not reveal errors
            return Ok(new { message = "If the email exists, a password reset link has been sent." });
        }
    }

    /// <summary>
    /// Password login
    /// </summary>
    [HttpPost("auth/password/login")]
    [AllowAnonymous]
    public async Task<ActionResult<ExchangeTokenResponseDto>> PasswordLogin([FromBody] PasswordLoginRequestDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return BadRequest(new { message = "Email is required" });
        }

        if (string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { message = "Password is required" });
        }

        _logger.LogInformation("Processing password login. Email length: {EmailLength}", request.Email.Length);

        try
        {
            // Call Nexa to authenticate with email/password
            var nexaResponse = await _nexaClient.PasswordLoginAsync(request.Email, request.Password);

            // Validate Nexa response structure
            if (nexaResponse == null || nexaResponse.User == null)
            {
                _logger.LogError("Nexa returned invalid password login response (missing user)");
                return StatusCode(500, new { message = "Invalid Nexa response format" });
            }

            if (string.IsNullOrEmpty(nexaResponse.User.UserId) || string.IsNullOrEmpty(nexaResponse.User.Email))
            {
                _logger.LogError("Nexa user missing required fields (userId or email)");
                return StatusCode(500, new { message = "Invalid Nexa response format" });
            }

            // Generate NextHire JWT (this is what FE must send to protected NextHire endpoints)
            var nextHireAccessToken = _jwtTokenService.GenerateToken(
                nexaResponse.User.UserId,
                nexaResponse.User.Email,
                nexaResponse.RequiresOrgSetup,
                nexaResponse.Organization?.GetOrgId()
            );

            // Store Nexa tokens for later proxy calls (password/set, initialize, onboarding)
            _logger.LogInformation(
                "Storing Nexa tokens for user {UserId} after successful password login. ExpiresAt: {ExpiresAt}",
                nexaResponse.User.UserId, nexaResponse.ExpiresAt);

            await _nexaTokens.PersistTokensAsync(
                nexaResponse.User.UserId,
                nexaResponse.AccessToken,
                nexaResponse.RefreshToken,
                nexaResponse.ExpiresAt);

            var response = new ExchangeTokenResponseDto
            {
                AccessToken = nextHireAccessToken,
                Nexa = new NexaTokensDto
                {
                    AccessToken = nexaResponse.AccessToken,
                    RefreshToken = nexaResponse.RefreshToken,
                    ExpiresAt = nexaResponse.ExpiresAt
                },
                RequiresOrgSetup = nexaResponse.RequiresOrgSetup,
                User = nexaResponse.User,
                Organization = nexaResponse.Organization,
                Features = nexaResponse.Features
            };

            return Ok(response);
        }
        catch (NexaAuthException ex)
        {
            _logger.LogWarning(ex, "Nexa password login failed with {StatusCode}: {ErrorMessage}", ex.StatusCode, ex.ErrorMessage);
            return Unauthorized(new { message = "Invalid credentials" });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error during Nexa password login");
            return StatusCode(502, new { message = "Nexa password login failed" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during password login");
            return StatusCode(500, new { message = "An error occurred during login" });
        }
    }

    /// <summary>
    /// Confirm password reset
    /// </summary>
    [HttpPost("auth/password/reset/confirm")]
    [AllowAnonymous]
    public async Task<IActionResult> ConfirmPasswordReset([FromBody] PasswordResetConfirmDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return BadRequest(new { message = "Token is required" });
        }

        if (string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return BadRequest(new { message = "New password is required" });
        }

        try
        {
            await _nexaClient.ConfirmPasswordResetAsync(request.Token, request.NewPassword);
            return Ok(new { message = "Password has been reset successfully" });
        }
        catch (NexaAuthException ex)
        {
            _logger.LogWarning(ex, "Password reset confirm failed with {StatusCode}: {ErrorMessage}", ex.StatusCode, ex.ErrorMessage);
            return Unauthorized(new { message = "Invalid or expired token" });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Error confirming password reset");
            return StatusCode(500, new { message = "An error occurred while resetting the password" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error confirming password reset");
            return StatusCode(500, new { message = "An error occurred while resetting the password" });
        }
    }

    /// <summary>
    /// Set password in Nexa
    /// </summary>
    [HttpPost("auth/password/set")]
    [Authorize]
    public async Task<IActionResult> SetPassword([FromBody] SetPasswordRequestDto request)
    {
        // Validate JWT claims - user must be authenticated
        var userId = User.FindFirst("nexa_user_id")?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("Set password request missing nexa_user_id claim");
            return Unauthorized(new { message = "Invalid token claims" });
        }

        _logger.LogInformation("Attempting to retrieve Nexa tokens for user {UserId}", userId);
        var nexaAccessToken = await _nexaTokens.GetValidAccessTokenAsync(userId);
        if (string.IsNullOrWhiteSpace(nexaAccessToken))
        {
            _logger.LogWarning("No valid Nexa token found for user {UserId}", userId);
            return Unauthorized(new { message = "Missing Nexa token, login again" });
        }

        // Validate input
        if (string.IsNullOrWhiteSpace(request.CurrentPassword))
        {
            return BadRequest(new { message = "Current password is required" });
        }

        if (string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return BadRequest(new { message = "New password is required" });
        }

        if (string.IsNullOrWhiteSpace(request.ConfirmPassword))
        {
            return BadRequest(new { message = "Confirm password is required" });
        }

        if (request.NewPassword != request.ConfirmPassword)
        {
            return BadRequest(new { message = "Passwords do not match" });
        }

        _logger.LogInformation("Setting password for user {UserId}. New password length: {PasswordLength}", userId, request.NewPassword.Length);

        try
        {
            // Call Nexa to set password
            var nexaResponse = await _nexaClient.SetPasswordAsync(
                request.CurrentPassword,
                request.NewPassword,
                request.ConfirmPassword,
                nexaAccessToken
            );

            // Return Nexa response directly to frontend
            return Ok(nexaResponse);
        }
        catch (NexaAuthException ex)
        {
            if (ex.StatusCode == 400 || ex.StatusCode == 401)
            {
                _logger.LogWarning(ex, "Nexa set password failed with {StatusCode}: {ErrorMessage}", ex.StatusCode, ex.ErrorMessage);
                return Unauthorized(new { message = ex.ErrorMessage });
            }
            else
            {
                _logger.LogError(ex, "Nexa auth error with status {StatusCode}: {ErrorMessage}", ex.StatusCode, ex.ErrorMessage);
                return StatusCode(500, new { message = "An error occurred while setting the password" });
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error during Nexa set password");
            return StatusCode(500, new { message = "An error occurred while setting the password" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error setting password");
            return StatusCode(500, new { message = "An error occurred while setting the password" });
        }
    }

    /// <summary>
    /// Initialize password in Nexa
    /// </summary>
    [HttpPost("auth/password/initialize")]
    [Authorize]
    public async Task<IActionResult> InitializePassword([FromBody] InitializePasswordRequestDto request)
    {
        // Validate JWT claims - user must be authenticated
        var userId = User.FindFirst("nexa_user_id")?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("Initialize password request missing nexa_user_id claim");
            return Unauthorized(new { message = "Invalid token claims" });
        }

        _logger.LogInformation("Attempting to retrieve Nexa tokens for user {UserId}", userId);
        var nexaAccessToken = await _nexaTokens.GetValidAccessTokenAsync(userId);
        if (string.IsNullOrWhiteSpace(nexaAccessToken))
        {
            _logger.LogWarning("No valid Nexa token found for user {UserId}", userId);
            return Unauthorized(new { message = "Missing Nexa token, login again" });
        }

        // Validate input
        if (string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return BadRequest(new { message = "New password is required" });
        }

        if (string.IsNullOrWhiteSpace(request.ConfirmPassword))
        {
            return BadRequest(new { message = "Confirm password is required" });
        }

        if (request.NewPassword != request.ConfirmPassword)
        {
            return BadRequest(new { message = "Passwords do not match" });
        }

        _logger.LogInformation("Initializing password for user {UserId}. New password length: {PasswordLength}", userId, request.NewPassword.Length);

        try
        {
            // Call Nexa to initialize password
            var nexaResponse = await _nexaClient.InitializePasswordAsync(
                request.NewPassword,
                request.ConfirmPassword,
                nexaAccessToken
            );

            // Return Nexa response directly to frontend
            return Ok(nexaResponse);
        }
        catch (NexaAuthException ex)
        {
            if (ex.StatusCode == 400 || ex.StatusCode == 401)
            {
                _logger.LogWarning(ex, "Nexa initialize password failed with {StatusCode}: {ErrorMessage}", ex.StatusCode, ex.ErrorMessage);
                return Unauthorized(new { message = ex.ErrorMessage });
            }
            else
            {
                _logger.LogError(ex, "Nexa auth error with status {StatusCode}: {ErrorMessage}", ex.StatusCode, ex.ErrorMessage);
                return StatusCode(500, new { message = "An error occurred while initializing the password" });
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error during Nexa initialize password");
            return StatusCode(500, new { message = "An error occurred while initializing the password" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error initializing password");
            return StatusCode(500, new { message = "An error occurred while initializing the password" });
        }
    }

    private static string GenerateRawToken()
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(tokenBytes).ToLowerInvariant();
    }

    private static string NormalizeMagicLinkToken(string raw)
    {
        var token = Uri.UnescapeDataString(raw.Trim());
        // Query strings sometimes turn '+' into space.
        if (token.Contains(' ') && !token.Contains('+'))
            token = token.Replace(' ', '+');
        return token;
    }
}

