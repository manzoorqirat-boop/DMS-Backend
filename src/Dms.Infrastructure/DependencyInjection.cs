using Dms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dms.Infrastructure;

/// <summary>
/// Wires the Infrastructure layer into the DI container from a single call in Program.cs.
/// Only the DbContext so far — storage driver, template validation wiring, and everything
/// else land here as the corresponding phases of the build are implemented.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

        services.AddDbContext<DmsDbContext>(options =>
        {
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__ef_migrations_history", DmsDbContext.Schema);

                // Same rationale as ERES: managed Postgres performs planned failovers and
                // maintenance restarts, which surface to users as request failures without a
                // retry strategy.
                npgsql.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorCodesToAdd: null);
            });

            options.EnableSensitiveDataLogging(false);
        });

        return services;
    }
}
