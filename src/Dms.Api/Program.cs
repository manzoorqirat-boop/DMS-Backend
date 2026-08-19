using Dms.Infrastructure;
using Dms.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddProblemDetails();

// CORS, auth, and everything else follow ERES's pattern once there's a frontend origin and
// an auth model actually decided for DMS — left out rather than guessed at, since a
// wrong-but-present policy is worse than an honestly absent one.

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

app.MapGet("/health/ready", async (DmsDbContext db, CancellationToken ct) =>
{
    var canConnect = await db.Database.CanConnectAsync(ct);
    return canConnect
        ? Results.Ok(new { status = "ready" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

app.Run();
