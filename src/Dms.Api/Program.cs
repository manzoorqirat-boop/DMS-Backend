using Dms.Api;
using Dms.Api.Endpoints;
using Dms.Application.Abstractions;
using Dms.Application.Templates;
using Dms.Infrastructure;
using Dms.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddProblemDetails();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

// Caps a multipart body at the same ceiling the application service enforces, so an
// oversized upload is rejected before being buffered in full.
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = TemplateRegistrationService.MaxTemplateBytes;
});

// CORS, auth, and everything else follow ERES's pattern once there's a frontend origin and
// an auth model actually decided for DMS — left out rather than guessed at, since a
// wrong-but-present policy is worse than an honestly absent one. Until auth lands, every
// write endpoint below returns 400 actor_unknown outside Development — see
// HttpContextCurrentUser for why that's the deliberate default.
var app = builder.Build();

app.UseStatusCodePages();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

app.MapGet("/health/ready", async (DmsDbContext db, CancellationToken ct) =>
{
    var canConnect = await db.Database.CanConnectAsync(ct);
    return canConnect
        ? Results.Ok(new { status = "ready" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

app.MapDocumentTypeEndpoints();
app.MapTemplateEndpoints();
app.MapOrganisationEndpoints();
app.MapDocumentEndpoints();
app.MapIntegrityEndpoints();
app.MapUserEndpoints();
app.MapRoleEndpoints();
app.MapNumberingEndpoints();
app.MapWorkflowEndpoints();
app.MapReviewEndpoints();
app.MapAuditEndpoints();

app.Run();
