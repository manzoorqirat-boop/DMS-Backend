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

    /// <summary>
    /// Creates the schema directly from the model when no migrations exist in the assembly.
    /// <para>
    /// <b>An escape hatch, not the intended path.</b> <c>EnsureCreated</c> builds the whole
    /// schema from the current model in one shot and writes nothing to
    /// <c>__ef_migrations_history</c> — which means EF afterwards has no idea what state the
    /// database is in, and the first real migration you ever add will fail against it. It
    /// exists here for exactly one situation: getting a brand-new, empty database standing up
    /// when there is no machine available that can run <c>dotnet ef migrations add</c>.
    /// </para>
    /// <para>
    /// It deliberately refuses to touch a database that already has tables, so it can never
    /// interfere with a properly migrated deployment. The moment a real migration exists in
    /// the assembly, the migration path below takes over and this is skipped entirely.
    /// </para>
    /// <para>
    /// Before this becomes a validated system, generate a real <c>InitialCreate</c> migration,
    /// drop the database this created, and let migrations build it — schema provenance is not
    /// a nicety in a Part 11 context, and "the app made the tables up on first boot" is not an
    /// answer anyone wants to give an auditor.
    /// </para>
    /// </summary>
    public const string EnsureCreatedKey = "Deploy:EnsureCreatedIfNoMigrations";

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

            await EnsureCreatedIfRequestedAsync(db, configuration, logger, cancellationToken);
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

    private static async Task EnsureCreatedIfRequestedAsync(
        DmsDbContext db,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!configuration.GetValue(EnsureCreatedKey, false))
        {
            return;
        }

        // Any applied migration at all means this database is under migration control, and
        // EnsureCreated must keep its hands off it entirely.
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
        if (applied.Count > 0)
        {
            logger.LogInformation(
                "{Count} migration(s) already applied; skipping EnsureCreated.", applied.Count);
            return;
        }

        logger.LogWarning(
            "No migrations exist in this build. Creating the schema directly from the model " +
            "({EnsureCreatedKey}=true). This writes nothing to the migrations history, so the " +
            "first real migration added later will NOT apply cleanly on top of it — generate an " +
            "InitialCreate migration and rebuild this database from it before go-live.",
            EnsureCreatedKey);

        // Returns false when the database already had tables — in which case nothing was
        // touched, which is exactly the desired behaviour on an existing deployment.
        var created = await db.Database.EnsureCreatedAsync(cancellationToken);

        logger.LogInformation(
            created
                ? "Schema created from the model."
                : "Database already contained tables; nothing was created.");

        if (created)
        {
            await ApplyAuditImmutabilityAsync(db, logger, cancellationToken);
        }
    }

    /// <summary>
    /// Runs the append-only trigger SQL that <c>EnsureCreated</c> knows nothing about.
    /// <para>
    /// EF builds only what its model describes, and these triggers are raw SQL — so on the
    /// EnsureCreated path they would otherwise simply never exist, leaving
    /// <c>dms.audit_events</c> quietly accepting UPDATE and DELETE. That is the one part of
    /// this fallback that would be genuinely dangerous to skip: the application-level guards
    /// in <c>AuditEvent</c> and <c>DmsDbContext</c> both live inside the app, and §11.10(e)
    /// asks for a trail that cannot be rewritten by someone holding the connection string.
    /// </para>
    /// <para>
    /// A failure here is logged and rethrown rather than swallowed. Starting up with an
    /// unprotected audit trail while reporting success would be worse than not starting at all.
    /// </para>
    /// </summary>
    private static async Task ApplyAuditImmutabilityAsync(
        DmsDbContext db,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "Persistence", "Migrations", "AuditImmutability.sql");

        if (!File.Exists(path))
        {
            logger.LogError(
                "AuditImmutability.sql was not found at {Path}. The audit and signature tables " +
                "have NO append-only protection. Check that the file is copied to the build " +
                "output (see Dms.Infrastructure.csproj).", path);

            throw new FileNotFoundException(
                "AuditImmutability.sql is required to protect the audit trail.", path);
        }

        var sql = await File.ReadAllTextAsync(path, cancellationToken);
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);

        logger.LogInformation("Audit-immutability triggers applied.");
    }
}
