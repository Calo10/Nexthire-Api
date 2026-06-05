using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using nexthire_api.Data;
using nexthire_api.Options;
using nexthire_api.Repositories;
using nexthire_api.Repositories.Sourcing;
using nexthire_api.Services;
using nexthire_api.Services.Sourcing;
using nexthire_api.Swagger;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddJsonFile(
        "appsettings.Development.local.json",
        optional: true,
        reloadOnChange: true);
}

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo 
    { 
        Title = "NextHire API", 
        Version = "v1",
        Description = "Backend API for NextHire - A hiring platform with Magic Link authentication via Nexa Identity Provider",
        Contact = new OpenApiContact
        {
            Name = "NextHire API Support",
            Email = "support@nexthire.com"
        },
        License = new OpenApiLicense
        {
            Name = "MIT License"
        }
    });
    
    // Include XML comments for better documentation
    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        c.IncludeXmlComments(xmlPath);
    }
    
    // Add JWT authentication to Swagger
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Enter 'Bearer' [space] and then your token.\n\nExample: \"Bearer 12345abcdef\"",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer",
        BearerFormat = "JWT"
    });
    
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
    
    // Enable annotations for better Swagger documentation
    c.EnableAnnotations();
    
    // Use fully qualified names to avoid conflicts
    c.CustomSchemaIds(type => type.FullName);

    // Explicit /api/tasks examples (so Swagger Example Value matches API shape)
    c.OperationFilter<TasksOperationExamples>();
    c.SchemaFilter<TasksSchemaExamples>();
});

// Configure JWT Authentication
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? throw new InvalidOperationException("Jwt:Issuer is not configured");
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? throw new InvalidOperationException("Jwt:Audience is not configured");
var jwtSigningKey = builder.Configuration["Jwt:SigningKey"] ?? throw new InvalidOperationException("Jwt:SigningKey is not configured");
var jwtSigningKeyBytes = Encoding.UTF8.GetBytes(jwtSigningKey);
var jwtSymmetricKey = new SymmetricSecurityKey(jwtSigningKeyBytes);

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtIssuer,
        ValidAudience = jwtAudience,
        // Always validate against our HS256 symmetric key (even if the incoming token has a 'kid')
        IssuerSigningKey = jwtSymmetricKey,
        IssuerSigningKeyResolver = (token, securityToken, kid, validationParameters) =>
            new[] { jwtSymmetricKey },
        ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
        ClockSkew = TimeSpan.Zero
    };
    
    // Add minimal logging for authentication failures
    options.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();

            var authHeader = context.Request.Headers.Authorization.ToString();
            var hasAuthHeader = !string.IsNullOrWhiteSpace(authHeader);
            var hasBearer = hasAuthHeader && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);

            // Do NOT log the token. Log only presence/shape to debug 401s safely.
            var token = hasBearer ? authHeader["Bearer ".Length..].Trim() : string.Empty;
            var tokenLength = token.Length;
            var tokenSegments = tokenLength > 0 ? token.Count(c => c == '.') + 1 : 0;

            string? alg = null;
            string? kid = null;
            string? iss = null;
            string? aud = null;
            if (tokenSegments >= 2)
            {
                try
                {
                    var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(token);
                    alg = jwt.Header.Alg;
                    kid = jwt.Header.Kid;
                    iss = jwt.Issuer;
                    aud = jwt.Audiences.FirstOrDefault();
                }
                catch
                {
                    // ignore parse failures; we'll still log shape info
                }
            }

            logger.LogInformation(
                "JWT message received. HasAuthHeader: {HasAuthHeader}, HasBearer: {HasBearer}, TokenLength: {TokenLength}, TokenSegments: {TokenSegments}, Alg: {Alg}, Kid: {Kid}, Iss: {Iss}, Aud: {Aud}",
                hasAuthHeader, hasBearer, tokenLength, tokenSegments, alg, kid, iss, aud);

            // Helpful clarity: Nexa-issued tokens should never be used to call NextHire protected endpoints.
            if (string.Equals(iss, "NexaApi", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(aud, "NexaApi", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "Received Nexa token on NextHire endpoint; expected NextHire JWT. Call /api/auth/consume first (or use NextHire accessToken from login/consume).");
            }

            return Task.CompletedTask;
        },
        OnAuthenticationFailed = context =>
        {
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
            var hasToken = !string.IsNullOrEmpty(context.Request.Headers["Authorization"].ToString());
            logger.LogWarning("JWT authentication failed. HasToken: {HasToken}, Error: {Error}", 
                hasToken, context.Exception.Message);
            return Task.CompletedTask;
        },
        OnChallenge = async context =>
        {
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
            var hasToken = !string.IsNullOrEmpty(context.Request.Headers["Authorization"].ToString());
            logger.LogWarning("JWT challenge triggered. HasToken: {HasToken}, Error: {Error}, ErrorDescription: {ErrorDescription}", 
                hasToken, context.Error, context.ErrorDescription);
            await ApplyCorsPolicyAsync(context.HttpContext, "AllowFrontend");
        }
    };
});

// Configure Authorization - No default requirement (endpoints decide individually)
builder.Services.AddAuthorization(options =>
{
    // FallbackPolicy is null - endpoints must explicitly use [Authorize] or [AllowAnonymous]
});

// Add custom services
builder.Services.AddScoped<IOrgUserService, OrgUserService>();
builder.Services.AddScoped<IOrgProvisioningService, OrgProvisioningService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<INexaAccessTokenResolver, NexaAccessTokenResolver>();
builder.Services.AddScoped<IJobService, JobService>();
builder.Services.AddScoped<IJobBotQuestionService, JobBotQuestionService>();
builder.Services.AddScoped<ICandidateService, CandidateService>();
builder.Services.AddScoped<IRoleService, RoleService>();
builder.Services.AddScoped<ITeamService, TeamService>();
builder.Services.AddScoped<IApplicationsService, ApplicationsService>();
builder.Services.AddScoped<IDocumentService, DocumentService>();
builder.Services.AddScoped<IResumeDocumentsUploader, ResumeDocumentsUploader>();
builder.Services.AddScoped<IWhatsAppTwilioMediaService, WhatsAppTwilioMediaService>();
builder.Services.AddScoped<IWhatsAppApplyResumeService, WhatsAppApplyResumeService>();
builder.Services.AddScoped<IWhatsAppInboundService, WhatsAppInboundService>();
builder.Services.AddScoped<IWhatsAppAiService, WhatsAppAiService>();

// DB connection factory (SQL Server)
builder.Services.AddSingleton<IDbConnectionFactory, SqlConnectionFactory>();

builder.Services.AddScoped<IDashboardRepository, DashboardRepository>();
builder.Services.AddScoped<IJobRepository, JobRepository>();
builder.Services.AddScoped<IJobBotQuestionRepository, JobBotQuestionRepository>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<ICandidateRepository, CandidateRepository>();
builder.Services.AddScoped<IRoleRepository, RoleRepository>();
builder.Services.AddScoped<ITeamRepository, TeamRepository>();
builder.Services.AddScoped<INexaTokenRepository, NexaTokenRepository>();
builder.Services.AddScoped<ITasksRepository, TasksRepository>();
builder.Services.AddScoped<INotesRepository, NotesRepository>();
builder.Services.AddScoped<IApplicationsRepository, ApplicationsRepository>();
builder.Services.AddScoped<IPipelineRepository, PipelineRepository>();
builder.Services.AddScoped<ITemplatesRepository, TemplatesRepository>();
builder.Services.AddScoped<ISourcingRepository, SourcingRepository>();
builder.Services.AddScoped<IWhatsAppInboundRepository, WhatsAppInboundRepository>();
builder.Services.AddScoped<ISourcingService, SourcingService>();
builder.Services.AddScoped<IMarketingMetaCampaignRepository, MarketingMetaCampaignRepository>();
builder.Services.AddHttpClient("MetaGraph", client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddHttpClient("AzureOpenAI", client =>
{
    var timeoutSeconds = builder.Configuration.GetValue<int?>("AzureOpenAI:ImageRequestTimeoutSeconds") ?? 300;
    if (timeoutSeconds < 30) timeoutSeconds = 30;
    if (timeoutSeconds > 600) timeoutSeconds = 600;
    client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
});
builder.Services.AddScoped<IMetaAdsService, MetaAdsService>();

// Add HttpClient for NexaClient with named client
builder.Services.AddHttpClient("Nexa", client =>
{
    var baseUrl = builder.Configuration["Nexa:BaseUrl"] ?? "http://localhost:5278/";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(
        builder.Configuration.GetValue<int?>("Nexa:RequestTimeoutSeconds") ?? 15);
});

// Documents function upload — no BaseAddress (full URL + x-functions-key avoids query-string loss)
builder.Services.AddHttpClient("Documents", client =>
{
    client.Timeout = TimeSpan.FromSeconds(120);
});

builder.Services.AddHttpClient("Twilio", client =>
{
    client.Timeout = TimeSpan.FromSeconds(120);
});

// DocumentService (download-url / analysis / etc)
builder.Services.AddHttpClient("DocumentService", client =>
{
    var baseUrl = builder.Configuration["DocumentService:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(baseUrl))
        client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddScoped<INexaClient, NexaClient>();
builder.Services.AddHttpClient<IEmailService, EmailService>();
builder.Services.AddHttpClient<INexaMessengerWhatsAppClient, NexaMessengerWhatsAppClient>();

// Add auth services
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddSingleton<INexaTokenStore, NexaTokenStore>();
builder.Services.AddSingleton<IAuthLinkTokenStore, AuthLinkTokenStore>();

// CORS: en Development acepta cualquier puerto en loopback (Vite puede usar 5174, [::1], etc.).
var corsOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "http://localhost:5173",
    "https://localhost:5173",
    "http://127.0.0.1:5173",
    "https://127.0.0.1:5173",
    "http://[::1]:5173",
    "https://[::1]:5173",
};
var configuredFront = builder.Configuration["Frontend:BaseUrl"]?.Trim().TrimEnd('/');
if (!string.IsNullOrEmpty(configuredFront))
    corsOrigins.Add(configuredFront);
foreach (var o in builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? Array.Empty<string>())
{
    var t = o.Trim().TrimEnd('/');
    if (!string.IsNullOrEmpty(t))
        corsOrigins.Add(t);
}

static bool IsLocalDevFrontendOrigin(string origin)
{
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        return false;

    if (uri.Scheme is not "http" and not "https")
        return false;

    if (uri.Host is "localhost" or "127.0.0.1" or "[::1]" or "0.0.0.0")
        return true;

    if (uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        return true;

    // Vite --host / pruebas desde el móvil en la misma red
    if (System.Net.IPAddress.TryParse(uri.Host, out var ip))
    {
        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && bytes.Length == 4)
        {
            if (bytes[0] == 10)
                return true;
            if (bytes[0] == 192 && bytes[1] == 168)
                return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                return true;
        }
    }

    return false;
}

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        if (builder.Environment.IsDevelopment())
        {
            // Dev: accept any browser origin (Vite ports, 127.0.0.1, LAN IP, etc.)
            policy.SetIsOriginAllowed(_ => true)
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials()
                  .SetPreflightMaxAge(TimeSpan.FromMinutes(10));
        }
        else
        {
            policy.WithOrigins(corsOrigins.ToArray())
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials();
        }
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "NextHire API v1");
    c.RoutePrefix = "swagger";
    c.DisplayRequestDuration();
    c.EnableDeepLinking();
    c.EnableFilter();
    c.EnableValidator();
    c.DocExpansion(Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.List);
    c.DefaultModelsExpandDepth(-1);
});

app.UseRouting();
app.UseCors("AllowFrontend");
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

if (app.Environment.IsDevelopment())
{
    app.Use(async (context, next) =>
    {
        var origin = context.Request.Headers.Origin.ToString();
        await next();
        if (!string.IsNullOrEmpty(origin)
            && !context.Response.Headers.ContainsKey("Access-Control-Allow-Origin"))
        {
            await ApplyCorsPolicyAsync(context, "AllowFrontend");
            var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
            logger.LogWarning(
                "CORS headers were missing; applied fallback. Origin={Origin} {Method} {Path} Status={Status}",
                origin,
                context.Request.Method,
                context.Request.Path,
                context.Response.StatusCode);
        }
    });
}

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

await Task.WhenAll(
    EnsureMarketingMetaCampaignsSchemaAsync(app.Services),
    EnsureSourcingCampaignsSchemaAsync(app.Services),
    EnsureSourcingSourceTypesAsync(app.Services),
    EnsureWhatsAppTenantMappingsAsync(app.Services),
    EnsureNhUsersSchemaAsync(app.Services),
    EnsureNexaUserTokensSchemaAsync(app.Services));

app.Lifetime.ApplicationStarted.Register(() =>
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    var urls = app.Urls.Count > 0 ? string.Join(", ", app.Urls) : "(default)";
    logger.LogInformation(
        "API ready at {Urls}. CORS={CorsMode}",
        urls,
        app.Environment.IsDevelopment()
            ? "Development (any origin)"
            : "Production (Cors:Origins + Frontend:BaseUrl only)");
});

app.Run();

static async Task ApplyCorsPolicyAsync(HttpContext context, string policyName)
{
    var corsService = context.RequestServices.GetRequiredService<Microsoft.AspNetCore.Cors.Infrastructure.ICorsService>();
    var policyProvider = context.RequestServices.GetRequiredService<Microsoft.AspNetCore.Cors.Infrastructure.ICorsPolicyProvider>();
    var policy = await policyProvider.GetPolicyAsync(context, policyName);
    if (policy is null)
        return;

    var result = corsService.EvaluatePolicy(context, policy);
    corsService.ApplyResult(result, context.Response);
}

static async Task EnsureWhatsAppTenantMappingsAsync(IServiceProvider services)
{
    using var scope = services.CreateScope();
    var repo = scope.ServiceProvider.GetRequiredService<IWhatsAppInboundRepository>();
    var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        await repo.EnsureTenantMappingsSchemaAsync();
        await repo.SyncTenantMappingsFromConfigAsync(configuration);
        logger.LogInformation("whatsapp_tenant_mappings table is ready.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to ensure whatsapp_tenant_mappings schema.");
    }
}

static async Task EnsureSourcingCampaignsSchemaAsync(IServiceProvider services)
{
    using var scope = services.CreateScope();
    var repo = scope.ServiceProvider.GetRequiredService<ISourcingRepository>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        await repo.EnsureCampaignsSchemaAsync();
        logger.LogInformation("sourcing_campaigns table is ready.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to ensure sourcing_campaigns schema.");
    }
}

static async Task EnsureSourcingSourceTypesAsync(IServiceProvider services)
{
    using var scope = services.CreateScope();
    var repo = scope.ServiceProvider.GetRequiredService<ISourcingRepository>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        await repo.EnsureDefaultSourceTypesAsync();
        logger.LogInformation("sourcing_source_types defaults are ready (public_apply, whatsapp, meta_ads).");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to seed sourcing_source_types defaults.");
    }
}

static async Task EnsureNexaUserTokensSchemaAsync(IServiceProvider services)
{
    using var scope = services.CreateScope();
    var repo = scope.ServiceProvider.GetRequiredService<INexaTokenRepository>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        await repo.EnsureSchemaAsync();
        logger.LogInformation("nexa_user_tokens table is ready.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to ensure nexa_user_tokens schema. Org invites may fail after API restart.");
    }
}

static async Task EnsureNhUsersSchemaAsync(IServiceProvider services)
{
    using var scope = services.CreateScope();
    var repo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        await repo.EnsureSchemaAsync();
        logger.LogInformation("nh_users schema is ready (nexa_user_id nullable for pending invites).");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to ensure nh_users schema. Org user invites may fail.");
    }
}

static async Task EnsureMarketingMetaCampaignsSchemaAsync(IServiceProvider services)
{
    using var scope = services.CreateScope();
    var repo = scope.ServiceProvider.GetRequiredService<IMarketingMetaCampaignRepository>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        await repo.EnsureSchemaAsync();
        logger.LogInformation("marketing_meta_campaigns table is ready.");
    }
    catch (Exception ex)
    {
        logger.LogError(
            ex,
            "Failed to ensure marketing_meta_campaigns schema. Meta campaign creates may fail to persist locally.");
    }
}

