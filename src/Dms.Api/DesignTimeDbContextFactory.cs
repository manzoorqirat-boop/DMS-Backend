using Dms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Dms.Api;

/// <summary>
/// Builds <see cref="DmsDbContext"/> for <c>dotnet ef</c> tooling — <c>migrations add</c>,
/// <c>database update</c>, <c>migrations script</c> — bypassing the application's own startup
/// pipeline entirely.
/// <para>
/// EF's tooling can, in principle, discover a DbContext by partially booting a minimal-API
/// <c>Program.cs</c>'s own host. That means successfully running JWT signing-key validation,
/// rate limiter setup, health check registration, and everything else <c>Program.cs</c> does
/// before <c>AddInfrastructure</c> ever runs — none of which a migration command needs, and
/// all of which is one more thing that can fail in a CI environment for a reason that has
/// nothing to do with the migration itself. This factory is EF's own documented escape hatch
/// for exactly this situation: a small, dedicated path that builds nothing but the DbContext,
/// using the same connection-string resolution (<see cref="DatabaseConnectionStringResolver"/>)
/// and the same Npgsql migrations-history table configuration the real application uses, so a
/// migration generated here matches what the running app will actually see.
/// </para>
/// <para>
/// Deliberately does <b>not</b> configure <c>EnableRetryOnFailure</c>, unlike the runtime
/// registration in <c>DependencyInjection.cs</c>. Retry-on-transient-failure is the right
/// default for a request handler, where an automatic retry is invisible to whoever's waiting
/// on it. It's the wrong default for applying a schema change: if something fails partway
/// through a migration, the operator running it should see that failure plainly and decide
/// what to do, rather than have it silently retried in a way that could compound a partial
/// DDL application. Lives in Dms.Api rather than Dms.Infrastructure so no project needs a new
/// package reference — Dms.Api already carries Microsoft.EntityFrameworkCore.Design for the
/// same tooling purpose, and every project in this solution deliberately keeps its package
/// surface minimal.
/// </para>
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DmsDbContext>
{
    public DmsDbContext CreateDbContext(string[] args)
    {
        // DATABASE_URL if set — Railway's own shape, and what the CI migration workflow
        // provides — falling back to ConnectionStrings__Postgres for local use. Same two
        // sources, same precedence, as AddInfrastructure uses at runtime.
        var databaseUrl = Environment.GetEnvironmentVariable(
            DatabaseConnectionStringResolver.DatabaseUrlEnvironmentVariable);
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");

        if (!string.IsNullOrWhiteSpace(databaseUrl) || !string.IsNullOrWhiteSpace(configured))
        {
            return Build(DatabaseConnectionStringResolver.Resolve(databaseUrl, configured));
        }

        // Nothing configured: fall back to a placeholder so the commands that need no database
        // still work. `migrations add`, `migrations list` and `migrations script` only read the
        // compiled model and never open a connection — generating a migration on a laptop with
        // no Postgres running is a completely normal thing to do, and this factory must not
        // stand in the way of it.
        //
        // A previous version tried to be smarter, throwing a helpful "you forgot to set
        // DATABASE_URL" error for commands that *do* connect, distinguishing them by looking
        // for "migrations" in `args`. That was built on an assumption about EF's design-time
        // invocation that turned out to be wrong: `args` is empty in practice, so every
        // command took the throw path and even `migrations list` failed. Reverted to the
        // simpler behaviour on purpose — `database update` without a connection string now
        // fails at connection time against localhost instead, which is a slightly less
        // pointed error but an honest one, and the workflow's own DATABASE_URL precheck
        // already catches that case earlier with a clear message anyway.
        return Build("Host=localhost;Port=5432;Database=dms_design_time;Username=postgres;Password=postgres");
    }

    private static DmsDbContext Build(string connectionString)
    {
        var optionsBuilder = new DbContextOptionsBuilder<DmsDbContext>();

        optionsBuilder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.MigrationsHistoryTable("__ef_migrations_history", DmsDbContext.Schema);
        });

        return new DmsDbContext(optionsBuilder.Options);
    }
}
