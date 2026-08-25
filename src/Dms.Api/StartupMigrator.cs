using Dms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dms.Api;

/// <summary>
/// Applies pending EF Core migrations at startup, if explicitly enabled.
/// <para>
/// Off by default, and it should stay off the moment this runs as more than one instance.
/// Railway's simplest deployment shape — one API instance, one Postgres — has no separate
/// "release phase" the way Heroku does, so running the migration as step one of the container
/// that's about to serve traffic is a reasonable convenience there. It stops being reasonable
/// the moment a second instance starts concurrently: both would race to apply the same
/// migration, and <c>__ef_migrations_history</c> provides no locking against that. A
/// dedicated one-off migration job — a Railway "run once" command, or the exact same call
/// from a developer's own machine pointed at the production connection string — is the
/// correct approach for anything beyond a single instance, and is also the only approach that
/// leaves a human-attributable record of when a schema change went out, which matters more
/// here than in most systems given everything else in this codebase treats "who did this and
/// when" as a first-class concern.
/// </para>
/// </summary>
public static class StartupMigrator
{
    public const string EnabledKey = "Deploy:RunMigrationsOnStartup";

    public static async Task RunIfEnabledAsync(
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        if (!configuration.GetValue(EnabledKey, false))
        {
            return;
        }

        var db = scope.ServiceProvider.GetRequiredService<DmsDbContext>();

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count == 0)
        {
            logger.LogInformation("No pending migrations.");
            return;
        }

        logger.LogWarning(
            "Applying {Count} pending migration(s) at startup: {Migrations}. " +
            "This is a single-instance convenience — see StartupMigrator's own remarks before " +
            "relying on it with more than one running instance.",
            pending.Count, string.Join(", ", pending));

        await db.Database.MigrateAsync(cancellationToken);

        logger.LogInformation("Migrations applied.");
    }
}
