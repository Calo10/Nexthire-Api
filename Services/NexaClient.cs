using System.Net.Http.Json;
using System.Text.Json;
using nexthire_api.DTOs;
using nexthire_api.Exceptions;

namespace nexthire_api.Services;

public interface INexaClient
{
    Task RequestMagicLinkAsync(string email, string? callbackUrl = null, CancellationToken cancellationToken = default);
    Task<NexaExchangeResponseDto> ExchangeTokenAsync(string token, CancellationToken cancellationToken = default);
    Task<NexaCreateOrgResponse> CreateOrgAsync(string name, string timezone, string nexaAccessToken, CancellationToken cancellationToken = default);
    Task RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default);
    Task ConfirmPasswordResetAsync(string token, string newPassword, CancellationToken cancellationToken = default);
    Task<NexaExchangeResponseDto> PasswordLoginAsync(string email, string password, CancellationToken cancellationToken = default);
    Task<(int StatusCode, string Content)> PasswordLoginRawAsync(string email, string password, CancellationToken cancellationToken = default);
    Task<object> SetPasswordAsync(string currentPassword, string newPassword, string confirmPassword, string nexaAccessToken, CancellationToken cancellationToken = default);
    Task<object> InitializePasswordAsync(string newPassword, string confirmPassword, string nexaAccessToken, CancellationToken cancellationToken = default);
    Task<NexaExchangeResponseDto> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default);
    Task<NexaOrgInviteDto?> SendOrgInviteAsync(Guid orgId, string email, string role, string nexaAccessToken, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NexaOrgInviteDto>> ListOrgInvitesAsync(Guid orgId, string nexaAccessToken, CancellationToken cancellationToken = default);
    Task<bool> RevokeOrgInviteAsync(Guid orgId, Guid inviteId, string nexaAccessToken, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NexaOrgMemberDto>> ListOrgMembersAsync(Guid orgId, string nexaAccessToken, CancellationToken cancellationToken = default);
    Task<NexaProvisionOrganizationResponse> ProvisionOrganizationAsync(
        string name,
        string timezone,
        string adminEmail,
        string? adminFullName,
        CancellationToken cancellationToken = default);
}

public class NexaClient : INexaClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<NexaClient> _logger;
    private readonly string _apiKey;
    private readonly string _magicLinkPath;
    private readonly string _consumePath;
    private readonly string _createOrgPath;
    private readonly string _provisionOrgPath;
    private readonly IConfiguration _configuration;

    public NexaClient(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<NexaClient> logger)
    {
        _httpClient = httpClientFactory.CreateClient("Nexa");
        _logger = logger;
        _configuration = configuration;
        _apiKey = configuration["Nexa:ApiKey"] ?? throw new InvalidOperationException("Nexa:ApiKey is not configured");
        _magicLinkPath = configuration["Nexa:MagicLinkPath"] ?? "v1/auth/magic-link";
        _consumePath = configuration["Nexa:ConsumePath"] ?? "v1/auth/consume";
        _createOrgPath = configuration["Nexa:CreateOrgPath"] ?? "v1/organizations";
        _provisionOrgPath = configuration["Nexa:ProvisionOrgPath"] ?? "v1/orgs/provision";

        _httpClient.DefaultRequestHeaders.Add("X-Nexa-Api-Key", _apiKey);
    }

    public async Task RequestMagicLinkAsync(string email, string? callbackUrl = null, CancellationToken cancellationToken = default)
    {
        object requestBody;
        if (callbackUrl != null)
        {
            requestBody = new { email, callbackUrl };
        }
        else
        {
            requestBody = new { email };
        }
        
        var requestUrl = $"/{_magicLinkPath.TrimStart('/')}";
        var fullUrl = $"{_httpClient.BaseAddress}{requestUrl.TrimStart('/')}";
        
        var response = await _httpClient.PostAsJsonAsync(requestUrl, requestBody, cancellationToken);
        
        // For 4xx errors (client errors), don't throw - don't reveal if email exists
        // For 5xx errors (server errors), throw to indicate service error
        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            
            _logger.LogWarning(
                "Nexa magic link request failed. URL: {Url}, StatusCode: {StatusCode}, Response: {Response}",
                fullUrl, response.StatusCode, errorContent);
            
            if (statusCode >= 400 && statusCode < 500)
            {
                // Client error (4xx) - don't reveal if email exists
                // Return normally - controller will return 200 OK
                return;
            }
            else if (statusCode >= 500)
            {
                // Server error (5xx) - throw to indicate service error
                throw new HttpRequestException($"Nexa API returned server error: {response.StatusCode}");
            }
        }
    }

    public async Task<NexaExchangeResponseDto> ExchangeTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        var request = new { token };
        var requestUrl = $"/{_consumePath.TrimStart('/')}";
        var fullUrl = $"{_httpClient.BaseAddress}{requestUrl.TrimStart('/')}";
        
        _logger.LogInformation("Calling Nexa consume endpoint. Token length: {TokenLength}", token?.Length ?? 0);
        
        var response = await _httpClient.PostAsJsonAsync(requestUrl, request, cancellationToken);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var statusCode = (int)response.StatusCode;
            
            // Try to extract error message from JSON response
            string errorMessage = "Unknown error";
            try
            {
                var errorJson = JsonSerializer.Deserialize<JsonElement>(errorContent);
                if (errorJson.TryGetProperty("error", out var errorProp))
                {
                    errorMessage = errorProp.GetString() ?? errorMessage;
                }
            }
            catch
            {
                // If parsing fails, use the raw content or default message
                errorMessage = !string.IsNullOrEmpty(errorContent) ? errorContent : errorMessage;
            }
            
            _logger.LogError(
                "Nexa token exchange failed. URL: {Url}, StatusCode: {StatusCode}, Response: {Response}",
                fullUrl, response.StatusCode, errorContent);
            
            // For 400 or 401, throw NexaAuthException
            if (statusCode == 400 || statusCode == 401)
            {
                throw new NexaAuthException(statusCode, errorMessage, errorContent);
            }
            
            // For other errors, throw generic HttpRequestException
            throw new HttpRequestException($"Nexa API returned error: {response.StatusCode}");
        }
        
        var jsonOptions = new JsonSerializerOptions 
        { 
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        
        // Read response as string first for logging
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogInformation("Nexa consume response received. Content length: {ContentLength}", responseContent.Length);
        
        var result = JsonSerializer.Deserialize<NexaExchangeResponseDto>(responseContent, jsonOptions);
        
        if (result == null)
        {
            throw new InvalidOperationException("Empty Nexa response");
        }

        return result;
    }

    public async Task<NexaCreateOrgResponse> CreateOrgAsync(string name, string timezone, string nexaAccessToken, CancellationToken cancellationToken = default)
    {
        var requestBody = new { name, timezone };
        var requestUrl = $"/{_createOrgPath.TrimStart('/')}";
        var fullUrl = $"{_httpClient.BaseAddress}{requestUrl.TrimStart('/')}";

        _logger.LogInformation("Creating organization in Nexa. Name length: {NameLength}, Timezone: {Timezone}", 
            name?.Length ?? 0, timezone);

        // Create a request message to set Authorization header
        var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUrl)
        {
            Content = JsonContent.Create(requestBody)
        };
        
        // Set Authorization header with Nexa access token
        requestMessage.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", nexaAccessToken);
        
        // Keep the X-Nexa-Api-Key header (if needed by Nexa)
        if (!string.IsNullOrEmpty(_apiKey))
        {
            requestMessage.Headers.Add("X-Nexa-Api-Key", _apiKey);
        }

        var response = await _httpClient.SendAsync(requestMessage, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var statusCode = (int)response.StatusCode;

            _logger.LogError(
                "Nexa create organization failed. URL: {Url}, StatusCode: {StatusCode}, Response length: {ResponseLength}",
                fullUrl, response.StatusCode, errorContent.Length);

            // For 401, throw NexaAuthException
            if (statusCode == 401)
            {
                string errorMessage = "Unauthorized";
                try
                {
                    var errorJson = JsonSerializer.Deserialize<JsonElement>(errorContent);
                    if (errorJson.TryGetProperty("error", out var errorProp))
                    {
                        errorMessage = errorProp.GetString() ?? errorMessage;
                    }
                }
                catch
                {
                    // Use default error message
                }
                throw new NexaAuthException(statusCode, errorMessage, errorContent);
            }

            // For 400/409, pass through status and body
            var exception = new HttpRequestException($"Nexa API returned error: {response.StatusCode}");
            exception.Data["StatusCode"] = response.StatusCode;
            exception.Data["ErrorBody"] = errorContent;
            throw exception;
        }

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogInformation("Nexa create organization response received. Content length: {ContentLength}", responseContent.Length);

        var result = JsonSerializer.Deserialize<NexaCreateOrgResponse>(responseContent, jsonOptions);

        if (result == null)
        {
            throw new InvalidOperationException("Empty Nexa create organization response");
        }

        _logger.LogInformation("Organization created successfully in Nexa");
        return result;
    }

    public async Task RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default)
    {
        var requestBody = new { email };
        var requestUrl = "/v1/auth/password/reset/request";
        var fullUrl = $"{_httpClient.BaseAddress}{requestUrl.TrimStart('/')}";

        _logger.LogInformation("Requesting password reset from Nexa for email. Email length: {EmailLength}", email?.Length ?? 0);

        var response = await _httpClient.PostAsJsonAsync(requestUrl, requestBody, cancellationToken);

        // For password reset requests, always return success to caller (don't reveal if email exists)
        // Log errors internally but don't throw
        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);

            _logger.LogWarning(
                "Nexa password reset request failed. URL: {Url}, StatusCode: {StatusCode}, Response length: {ResponseLength}",
                fullUrl, response.StatusCode, errorContent.Length);

            // For 4xx errors, don't throw - don't reveal if email exists
            if (statusCode >= 400 && statusCode < 500)
            {
                return;
            }
            // For 5xx errors, log but still return success to caller
            else if (statusCode >= 500)
            {
                _logger.LogError("Nexa password reset request failed with server error {StatusCode}", response.StatusCode);
                return;
            }
        }
        else
        {
            _logger.LogInformation("Password reset request sent successfully to Nexa");
        }
    }

    public async Task ConfirmPasswordResetAsync(string token, string newPassword, CancellationToken cancellationToken = default)
    {
        var requestBody = new { token, newPassword };
        var requestUrl = "/v1/auth/password/reset/confirm";
        var fullUrl = $"{_httpClient.BaseAddress}{requestUrl.TrimStart('/')}";

        _logger.LogInformation("Confirming password reset in Nexa. Token length: {TokenLength}, Password length: {PasswordLength}",
            token?.Length ?? 0, newPassword?.Length ?? 0);

        var response = await _httpClient.PostAsJsonAsync(requestUrl, requestBody, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var statusCode = (int)response.StatusCode;

            _logger.LogError(
                "Nexa password reset confirm failed. URL: {Url}, StatusCode: {StatusCode}, Response length: {ResponseLength}",
                fullUrl, response.StatusCode, errorContent.Length);

            // For 400/401, throw exception to indicate invalid/expired token
            if (statusCode == 400 || statusCode == 401)
            {
                string errorMessage = "Invalid or expired token";
                try
                {
                    var errorJson = JsonSerializer.Deserialize<JsonElement>(errorContent);
                    if (errorJson.TryGetProperty("error", out var errorProp))
                    {
                        errorMessage = errorProp.GetString() ?? errorMessage;
                    }
                }
                catch
                {
                    // Use default error message
                }
                throw new NexaAuthException(statusCode, errorMessage, errorContent);
            }

            // For other errors, throw generic exception
            var exception = new HttpRequestException($"Nexa API returned error: {response.StatusCode}");
            exception.Data["StatusCode"] = response.StatusCode;
            exception.Data["ErrorBody"] = errorContent;
            throw exception;
        }

        _logger.LogInformation("Password reset confirmed successfully in Nexa");
    }

    public async Task<NexaExchangeResponseDto> PasswordLoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        var requestBody = new { email, password };
        var requestUrl = "/v1/auth/password/login";
        var fullUrl = $"{_httpClient.BaseAddress}{requestUrl.TrimStart('/')}";

        _logger.LogInformation("Attempting password login via Nexa. Email length: {EmailLength}, Password length: {PasswordLength}",
            email?.Length ?? 0, password?.Length ?? 0);

        var response = await _httpClient.PostAsJsonAsync(requestUrl, requestBody, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var statusCode = (int)response.StatusCode;

            _logger.LogError(
                "Nexa password login failed. URL: {Url}, StatusCode: {StatusCode}, Response length: {ResponseLength}",
                fullUrl, response.StatusCode, errorContent.Length);

            // If Nexa returns 404, the endpoint doesn't exist
            if (statusCode == 404)
            {
                throw new HttpRequestException("Nexa password login endpoint not found")
                {
                    Data = { ["StatusCode"] = response.StatusCode, ["IsNotImplemented"] = true }
                };
            }

            // For 400/401, throw NexaAuthException
            if (statusCode == 400 || statusCode == 401)
            {
                string errorMessage = "Invalid credentials";
                try
                {
                    var errorJson = JsonSerializer.Deserialize<JsonElement>(errorContent);
                    if (errorJson.TryGetProperty("error", out var errorProp))
                    {
                        errorMessage = errorProp.GetString() ?? errorMessage;
                    }
                }
                catch
                {
                    // Use default error message
                }
                throw new NexaAuthException(statusCode, errorMessage, errorContent);
            }

            // For other errors, throw generic exception
            var exception = new HttpRequestException($"Nexa API returned error: {response.StatusCode}");
            exception.Data["StatusCode"] = response.StatusCode;
            exception.Data["ErrorBody"] = errorContent;
            throw exception;
        }

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogInformation("Nexa password login response received. Content length: {ContentLength}", responseContent.Length);

        var result = JsonSerializer.Deserialize<NexaExchangeResponseDto>(responseContent, jsonOptions);

        if (result == null)
        {
            throw new InvalidOperationException("Empty Nexa password login response");
        }

        _logger.LogInformation("Password login successful in Nexa");
        return result;
    }

    public async Task<(int StatusCode, string Content)> PasswordLoginRawAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        var requestBody = new { email, password };
        var requestUrl = "/v1/auth/password/login";
        var fullUrl = $"{_httpClient.BaseAddress}{requestUrl.TrimStart('/')}";

        _logger.LogInformation("Attempting password login via Nexa (raw). Email length: {EmailLength}, Password length: {PasswordLength}",
            email?.Length ?? 0, password?.Length ?? 0);

        var response = await _httpClient.PostAsJsonAsync(requestUrl, requestBody, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var statusCode = (int)response.StatusCode;

        _logger.LogInformation("Nexa password login response. StatusCode: {StatusCode}, Content length: {ContentLength}",
            statusCode, content.Length);

        return (statusCode, content);
    }

    public async Task<object> SetPasswordAsync(string currentPassword, string newPassword, string confirmPassword, string nexaAccessToken, CancellationToken cancellationToken = default)
    {
        var requestBody = new { currentPassword, newPassword, confirmPassword };
        var requestUrl = "/v1/auth/password/set";
        var fullUrl = $"{_httpClient.BaseAddress}{requestUrl.TrimStart('/')}";

        _logger.LogInformation("Setting password in Nexa. New password length: {PasswordLength}", newPassword?.Length ?? 0);

        // Create a request message to set Authorization header
        var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUrl)
        {
            Content = JsonContent.Create(requestBody)
        };
        
        // Set Authorization header with Nexa access token
        requestMessage.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", nexaAccessToken);
        
        // Keep the X-Nexa-Api-Key header (if needed by Nexa)
        if (!string.IsNullOrEmpty(_apiKey))
        {
            requestMessage.Headers.Add("X-Nexa-Api-Key", _apiKey);
        }

        var response = await _httpClient.SendAsync(requestMessage, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var statusCode = (int)response.StatusCode;

            _logger.LogError(
                "Nexa set password failed. URL: {Url}, StatusCode: {StatusCode}, Response length: {ResponseLength}",
                fullUrl, response.StatusCode, errorContent.Length);

            // For 400/401, throw NexaAuthException
            if (statusCode == 400 || statusCode == 401)
            {
                string errorMessage = "Invalid request";
                try
                {
                    var errorJson = JsonSerializer.Deserialize<JsonElement>(errorContent);
                    if (errorJson.TryGetProperty("error", out var errorProp))
                    {
                        errorMessage = errorProp.GetString() ?? errorMessage;
                    }
                }
                catch
                {
                    // Use default error message
                }
                throw new NexaAuthException(statusCode, errorMessage, errorContent);
            }

            // For other errors, throw generic exception
            var exception = new HttpRequestException($"Nexa API returned error: {response.StatusCode}");
            exception.Data["StatusCode"] = response.StatusCode;
            exception.Data["ErrorBody"] = errorContent;
            throw exception;
        }

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogInformation("Nexa set password response received. Content length: {ContentLength}", responseContent.Length);

        // Deserialize as object to pass through whatever Nexa returns
        var result = JsonSerializer.Deserialize<object>(responseContent, jsonOptions);

        if (result == null)
        {
            throw new InvalidOperationException("Empty Nexa set password response");
        }

        _logger.LogInformation("Password set successfully in Nexa");
        return result;
    }

    public async Task<object> InitializePasswordAsync(string newPassword, string confirmPassword, string nexaAccessToken, CancellationToken cancellationToken = default)
    {
        var requestBody = new { newPassword, confirmPassword };
        var requestUrl = "/v1/auth/password/initialize";
        var fullUrl = $"{_httpClient.BaseAddress}{requestUrl.TrimStart('/')}";

        _logger.LogInformation("Initializing password in Nexa. New password length: {PasswordLength}", newPassword?.Length ?? 0);

        // Create a request message to set Authorization header
        var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUrl)
        {
            Content = JsonContent.Create(requestBody)
        };
        
        // Set Authorization header with Nexa access token
        requestMessage.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", nexaAccessToken);
        
        // Keep the X-Nexa-Api-Key header (if needed by Nexa)
        if (!string.IsNullOrEmpty(_apiKey))
        {
            requestMessage.Headers.Add("X-Nexa-Api-Key", _apiKey);
        }

        var response = await _httpClient.SendAsync(requestMessage, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var statusCode = (int)response.StatusCode;

            _logger.LogError(
                "Nexa initialize password failed. URL: {Url}, StatusCode: {StatusCode}, Response length: {ResponseLength}",
                fullUrl, response.StatusCode, errorContent.Length);

            // For 400/401, throw NexaAuthException
            if (statusCode == 400 || statusCode == 401)
            {
                string errorMessage = "Invalid request";
                try
                {
                    var errorJson = JsonSerializer.Deserialize<JsonElement>(errorContent);
                    if (errorJson.TryGetProperty("error", out var errorProp))
                    {
                        errorMessage = errorProp.GetString() ?? errorMessage;
                    }
                }
                catch
                {
                    // Use default error message
                }
                throw new NexaAuthException(statusCode, errorMessage, errorContent);
            }

            // For other errors, throw generic exception
            var exception = new HttpRequestException($"Nexa API returned error: {response.StatusCode}");
            exception.Data["StatusCode"] = response.StatusCode;
            exception.Data["ErrorBody"] = errorContent;
            throw exception;
        }

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogInformation("Nexa initialize password response received. Content length: {ContentLength}", responseContent.Length);

        // Deserialize as object to pass through whatever Nexa returns
        var result = JsonSerializer.Deserialize<object>(responseContent, jsonOptions);

        if (result == null)
        {
            throw new InvalidOperationException("Empty Nexa initialize password response");
        }

        _logger.LogInformation("Password initialized successfully in Nexa");
        return result;
    }

    public async Task<NexaExchangeResponseDto> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var requestBody = new { refreshToken };
        var requestUrl = "/v1/auth/refresh";
        var fullUrl = $"{_httpClient.BaseAddress}{requestUrl.TrimStart('/')}";

        _logger.LogInformation("Refreshing Nexa token. Refresh token length: {RefreshTokenLength}", refreshToken?.Length ?? 0);

        var response = await _httpClient.PostAsJsonAsync(requestUrl, requestBody, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var statusCode = (int)response.StatusCode;

            _logger.LogError(
                "Nexa token refresh failed. URL: {Url}, StatusCode: {StatusCode}, Response length: {ResponseLength}",
                fullUrl, response.StatusCode, errorContent.Length);

            // For 400/401, throw NexaAuthException
            if (statusCode == 400 || statusCode == 401)
            {
                string errorMessage = "Invalid refresh token";
                try
                {
                    var errorJson = JsonSerializer.Deserialize<JsonElement>(errorContent);
                    if (errorJson.TryGetProperty("error", out var errorProp))
                    {
                        errorMessage = errorProp.GetString() ?? errorMessage;
                    }
                }
                catch
                {
                    // Use default error message
                }
                throw new NexaAuthException(statusCode, errorMessage, errorContent);
            }

            // For 404, endpoint doesn't exist
            if (statusCode == 404)
            {
                throw new HttpRequestException("Nexa refresh endpoint not found")
                {
                    Data = { ["StatusCode"] = response.StatusCode, ["IsNotImplemented"] = true }
                };
            }

            // For other errors, throw generic exception
            var exception = new HttpRequestException($"Nexa API returned error: {response.StatusCode}");
            exception.Data["StatusCode"] = response.StatusCode;
            exception.Data["ErrorBody"] = errorContent;
            throw exception;
        }

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogInformation("Nexa token refresh response received. Content length: {ContentLength}", responseContent.Length);

        var result = JsonSerializer.Deserialize<NexaExchangeResponseDto>(responseContent, jsonOptions);

        if (result == null)
        {
            throw new InvalidOperationException("Empty Nexa refresh response");
        }

        _logger.LogInformation("Token refreshed successfully in Nexa");
        return result;
    }

    public async Task<NexaOrgInviteDto?> SendOrgInviteAsync(
        Guid orgId,
        string email,
        string role,
        string nexaAccessToken,
        CancellationToken cancellationToken = default)
    {
        var requestUrl = $"/v1/orgs/{orgId}/invites";
        var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUrl)
        {
            Content = JsonContent.Create(new { email, role })
        };
        requestMessage.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", nexaAccessToken);
        if (!string.IsNullOrEmpty(_apiKey))
            requestMessage.Headers.Add("X-Nexa-Api-Key", _apiKey);

        var response = await _httpClient.SendAsync(requestMessage, cancellationToken);
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Nexa org invite failed. OrgId={OrgId} Status={Status} BodyLength={Length}",
                orgId, response.StatusCode, responseContent.Length);
            var statusCode = (int)response.StatusCode;
            if (statusCode is 400 or 401 or 403 or 404 or 409)
            {
                var ex = new HttpRequestException($"Nexa API returned error: {response.StatusCode}");
                ex.Data["StatusCode"] = response.StatusCode;
                ex.Data["ErrorBody"] = responseContent;
                throw ex;
            }

            throw new HttpRequestException($"Nexa API returned error: {response.StatusCode}");
        }

        return JsonSerializer.Deserialize<NexaOrgInviteDto>(responseContent, JsonOptions)
               ?? throw new InvalidOperationException("Empty Nexa invite response");
    }

    public async Task<IReadOnlyList<NexaOrgInviteDto>> ListOrgInvitesAsync(
        Guid orgId,
        string nexaAccessToken,
        CancellationToken cancellationToken = default)
    {
        var requestUrl = $"/v1/orgs/{orgId}/invites";
        var requestMessage = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        requestMessage.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", nexaAccessToken);
        if (!string.IsNullOrEmpty(_apiKey))
            requestMessage.Headers.Add("X-Nexa-Api-Key", _apiKey);

        var response = await _httpClient.SendAsync(requestMessage, cancellationToken);
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Nexa list invites failed. OrgId={OrgId} Status={Status}",
                orgId, response.StatusCode);
            var ex = new HttpRequestException($"Nexa API returned error: {response.StatusCode}");
            ex.Data["StatusCode"] = response.StatusCode;
            ex.Data["ErrorBody"] = responseContent;
            throw ex;
        }

        return JsonSerializer.Deserialize<List<NexaOrgInviteDto>>(responseContent, JsonOptions) ?? [];
    }

    public async Task<bool> RevokeOrgInviteAsync(
        Guid orgId,
        Guid inviteId,
        string nexaAccessToken,
        CancellationToken cancellationToken = default)
    {
        var requestUrl = $"/v1/orgs/{orgId}/invites/{inviteId}";
        var requestMessage = new HttpRequestMessage(HttpMethod.Delete, requestUrl);
        requestMessage.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", nexaAccessToken);
        if (!string.IsNullOrEmpty(_apiKey))
            requestMessage.Headers.Add("X-Nexa-Api-Key", _apiKey);

        var response = await _httpClient.SendAsync(requestMessage, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return false;

        if (!response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "Nexa revoke invite failed. OrgId={OrgId} InviteId={InviteId} Status={Status}",
                orgId, inviteId, response.StatusCode);
            var ex = new HttpRequestException($"Nexa API returned error: {response.StatusCode}");
            ex.Data["StatusCode"] = response.StatusCode;
            ex.Data["ErrorBody"] = responseContent;
            throw ex;
        }

        return true;
    }

    public async Task<IReadOnlyList<NexaOrgMemberDto>> ListOrgMembersAsync(
        Guid orgId,
        string nexaAccessToken,
        CancellationToken cancellationToken = default)
    {
        var requestUrl = $"/v1/orgs/{orgId}/members";
        var requestMessage = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        requestMessage.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", nexaAccessToken);
        if (!string.IsNullOrEmpty(_apiKey))
            requestMessage.Headers.Add("X-Nexa-Api-Key", _apiKey);

        var response = await _httpClient.SendAsync(requestMessage, cancellationToken);
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Nexa list members failed. OrgId={OrgId} Status={Status}",
                orgId, response.StatusCode);
            var ex = new HttpRequestException($"Nexa API returned error: {response.StatusCode}");
            ex.Data["StatusCode"] = response.StatusCode;
            ex.Data["ErrorBody"] = responseContent;
            throw ex;
        }

        return JsonSerializer.Deserialize<List<NexaOrgMemberDto>>(responseContent, JsonOptions) ?? [];
    }

    public async Task<NexaProvisionOrganizationResponse> ProvisionOrganizationAsync(
        string name,
        string timezone,
        string adminEmail,
        string? adminFullName,
        CancellationToken cancellationToken = default)
    {
        var provisioningApiKey = _configuration["Provisioning:ApiKey"]?.Trim();
        if (string.IsNullOrWhiteSpace(provisioningApiKey))
            throw new InvalidOperationException("Provisioning:ApiKey is not configured.");

        var requestUrl = $"/{_provisionOrgPath.TrimStart('/')}";
        var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUrl)
        {
            Content = JsonContent.Create(new
            {
                name,
                timezone,
                adminEmail,
                adminFullName
            })
        };
        requestMessage.Headers.Add("X-Api-Key", provisioningApiKey);

        var response = await _httpClient.SendAsync(requestMessage, cancellationToken);
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Nexa provision org failed. Status={Status} BodyLength={Length}",
                response.StatusCode, responseContent.Length);

            var ex = new HttpRequestException($"Nexa API returned error: {response.StatusCode}");
            ex.Data["StatusCode"] = response.StatusCode;
            ex.Data["ErrorBody"] = responseContent;
            throw ex;
        }

        var result = JsonSerializer.Deserialize<NexaProvisionOrganizationResponse>(responseContent, JsonOptions)
                     ?? throw new InvalidOperationException("Empty Nexa provision response");

        _logger.LogInformation(
            "Nexa org provisioned. OrgId={OrgId} AdminUserId={AdminUserId} AdminCreated={AdminCreated}",
            result.OrganizationId, result.AdminUserId, result.AdminUserCreated);

        return result;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

