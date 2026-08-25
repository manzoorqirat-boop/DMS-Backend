using System.Security.Claims;
using System.Text;
using Dms.Api;
using Dms.Api.Auth;
using Dms.Api.Endpoints;
using Dms.Application.Abstractions;
using Dms.Application.Auth;
using Dms.Application.Templates;
using Dms.Infrastructure;
using Dms.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddProblemDetails();

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

app.UseStatusCodePages();

if (corsOrigins.Length > 0)
{
    app.UseCors();
}

app.UseAuthentication();
app.UseAuthorization();

// Before serving traffic: without a seeded administrator on a fresh database, nobody can log
// in and no endpoint would let them, because authorisation denies by default.
await BootstrapSeeder.SeedAsync(
    app.Services,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(BootstrapSeeder)),
    CancellationToken.None);

// Health checks stay anonymous: a load balancer has no token, and a readiness probe that
// requires one fails for the wrong reason during an outage.
app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();

app.MapGet("/health/ready", async (DmsDbContext db, CancellationToken ct) =>
{
    var canConnect = await db.Database.CanConnectAsync(ct);
    return canConnect
        ? Results.Ok(new { status = "ready" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
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
app.MapEditingEndpoints();
app.MapReviewEndpoints();
app.MapAuditEndpoints();

app.Run();
