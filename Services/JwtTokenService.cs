using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace nexthire_api.Services;

public interface IJwtTokenService
{
    string GenerateToken(string nexaUserId, string email, bool requiresOrgSetup, string? nexaOrgId = null);
}

public class JwtTokenService : IJwtTokenService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<JwtTokenService> _logger;

    public JwtTokenService(IConfiguration configuration, ILogger<JwtTokenService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public string GenerateToken(string nexaUserId, string email, bool requiresOrgSetup, string? nexaOrgId = null)
    {
        var issuer = _configuration["Jwt:Issuer"] ?? throw new InvalidOperationException("Jwt:Issuer is not configured");
        var audience = _configuration["Jwt:Audience"] ?? throw new InvalidOperationException("Jwt:Audience is not configured");
        var signingKey = _configuration["Jwt:SigningKey"] ?? throw new InvalidOperationException("Jwt:SigningKey is not configured");
        var expiresMinutes = int.Parse(_configuration["Jwt:ExpiresMinutes"] ?? "60");

        // Safe debug log (do NOT log the secret). Helps confirm generator config matches validator config.
        _logger.LogInformation(
            "Generating NextHire JWT. Issuer: {Issuer}, Audience: {Audience}, Alg: {Alg}, SigningKeyLength: {KeyLength}, ExpiresMinutes: {ExpiresMinutes}",
            issuer, audience, SecurityAlgorithms.HmacSha256, signingKey.Length, expiresMinutes);

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            // Standard subject claim (used by business endpoints expecting Guid user id).
            // NOTE: Depending on inbound mapping this may show up as ClaimTypes.NameIdentifier.
            new(JwtRegisteredClaimNames.Sub, nexaUserId),

            new("nexa_user_id", nexaUserId),
            new(ClaimTypes.Email, email),
            new("requires_org_setup", requiresOrgSetup.ToString().ToLowerInvariant()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        if (!string.IsNullOrWhiteSpace(nexaOrgId))
        {
            // Canonical org claim for NextHire business endpoints.
            claims.Add(new Claim("org_id", nexaOrgId));

            // Keep existing claim for backward compatibility with current controllers.
            claims.Add(new Claim("nexa_org_id", nexaOrgId));
        }

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(expiresMinutes),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

