using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using Dms.Api;
using Dms.Api.Auth;
using Dms.Api.Endpoints;
using Dms.Application.Abstractions;
using Dms.Application.Auth;
using Dms.Application.Templates;
using Dms.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Railway (and Render, Heroku-style platforms generally) assigns a container an arbitrary
// port at runtime via the PORT environment variable and routes traffic to it — the
// Dockerfile's own ASPNETCORE_URLS=http://+:8080 is only a sensible default for local/Compose
// use, where nothing sets PORT. Binding to it explicitly here means this works correctly on
// Railway without depending on a platform-specific override of ASPNETCORE_URLS elsewhere.
if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port)
{
    builder.WebHost.UseUrls($"http://+:{port}");
}

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddDmsOpenApi();
builder.Services.AddDmsRateLimiting(builder.Configuration);

// Without this, every enum (TemplateStatus, DocumentStatus, DispositionAction, ...) serializes
// as its raw ordinal number ("2") instead of its name ("ValidationFailed"). Every DTO record
// and every frontend type file was written assuming string enums — this is what actually makes
// that true, for both Results.Ok(...) and WriteAsJsonAsync responses.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// A named client for outbound health probes, kept separate from the document-server fetch
// client so a probe timeout can't be confused with a save failure.
builder.Services.AddHttpClient("health");

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database")
    .AddCheck<BlobStoreHealthCheck>("blob-store")
    .AddCheck<DocumentServerHealthCheck>("document-server");

builder.Services.AddHostedService<DailyReminderService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

builder.Services.AddSingleton<IAuthPolicy, AuthPolicy>();
builder.Services.AddSingleton<IAccessTokenIssuer, JwtAccessTokenIssuer>();
builder.Services.AddScoped<AuthService>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration[JwtConfig.IssuerKey] ?? JwtConfig.DefaultIssuer,
            ValidAudience = builder.Configuration[JwtConfig.AudienceKey] ?? JwtConfig.DefaultAudience,
            IssuerSigningKey = JwtConfig.ReadSigningKey(builder.Configuration),

            // HttpContextCurrentUser reads User.Identity.Name, and every audit record is
            // attributed from it — so the claim that populates it has to be pinned rather
            // than left to handler defaults.
            NameClaimType = ClaimTypes.Name,

            // No tolerance for expired tokens. The default five minutes is a convenience that
            // means a revoked or expired session keeps working past the moment the records say
            // it stopped.
            ClockSkew = TimeSpan.Zero,
        };
    });

builder.Services.AddAuthorization(options =>
{
    // Deny by default. Every endpoint requires authentication unless it explicitly opts out
    // with AllowAnonymous — the reverse arrangement means a new endpoint added later is public
    // until someone remembers to protect it.
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// Origins are configured per deployment; an empty list disables CORS entirely rather than
// falling back to a permissive default.
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
if (corsOrigins.Length > 0)
{
    builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
        .WithOrigins(corsOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        // Watermark and scan-code headers are on the print response and are useless to a
        // browser client unless exposed.
        .WithExposedHeaders("X-Copy-Watermark", "X-Copy-Scan-Code", "X-Copy-Print-Sequence", "X-Copy-Watermarked")));
}

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = TemplateRegistrationService.MaxTemplateBytes;
});

var app = builder.Build();

// First in the pipeline: an exception thrown by anything downstream, including
// authentication, has to reach this rather than surfacing as an empty 500.
app.UseExceptionHandler();

app.UseStatusCodePages();
app.UseDmsOpenApi();

if (corsOrigins.Length > 0)
{
    app.UseCors();
}

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

// Off by default — see StartupMigrator for why an unconditional auto-migrate on every boot
// is the wrong default for anything beyond a single-instance deployment like Railway's.
// Must run before BootstrapSeeder: seeding writes rows, which needs the schema to exist first.
await StartupMigrator.RunIfEnabledAsync(
    app.Services,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(StartupMigrator)),
    CancellationToken.None);

// Before serving traffic: without a seeded administrator on a fresh database, nobody can log
// in and no endpoint would let them, because authorisation denies by default.
await BootstrapSeeder.SeedAsync(
    app.Services,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(BootstrapSeeder)),
    CancellationToken.None);

// Health checks stay anonymous: a load balancer has no token, and a readiness probe that
// requires one fails for the wrong reason during an outage.
app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    // Degraded still passes readiness. The document server being down means editing fails, not
    // that this instance should be pulled out of the load balancer.
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status200OK,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
    },
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
            }),
        });
    },
}).AllowAnonymous();

app.MapAuthEndpoints();
app.MapDocumentTypeEndpoints();
app.MapTemplateEndpoints();
app.MapOrganisationEndpoints();
app.MapDocumentEndpoints();
app.MapIntegrityEndpoints();
app.MapUserEndpoints();
app.MapRoleEndpoints();
app.MapNumberingEndpoints();
app.MapWorkflowEndpoints();
app.MapMetadataEndpoints();
app.MapLifecycleEndpoints();
app.MapDistributionEndpoints();
app.MapNotificationEndpoints();

// Mapped only when a document server is configured, matching the conditional registration of
// EditingService and its dependencies in AddInfrastructure. A route that exists but throws on
// dependency injection is a worse answer than no route at all for a feature that is
// deliberately switched off — and StartSessionAsync's own `editor_not_configured` guard can
// only speak once the service resolves, which it can't here.
if (!string.IsNullOrWhiteSpace(app.Configuration[Dms.Infrastructure.Editing.EditorConfig.UrlKey]))
{
    app.MapEditingEndpoints();
}

app.MapReviewEndpoints();
app.MapAuditEndpoints();
app.MapExportEndpoints();

app.Run();
