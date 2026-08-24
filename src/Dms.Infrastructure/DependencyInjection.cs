using Dms.Application.Abstractions;
using Dms.Application.DocumentTypes;
using Dms.Application.Documents;
using Dms.Application.Templates;
using Dms.Infrastructure.Persistence;
using Dms.Infrastructure.Persistence.Repositories;
using Dms.Infrastructure.Storage;
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

        services.AddScoped<IDocumentTypeRepository, DocumentTypeRepository>();
        services.AddScoped<ITemplateRepository, TemplateRepository>();
        services.AddScoped<ISiteRepository, SiteRepository>();
        services.AddScoped<IDepartmentRepository, DepartmentRepository>();
        services.AddScoped<IControlledDocumentRepository, ControlledDocumentRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        var templateRoot = configuration[TemplateStorageConfig.RootPathKey]
            ?? TemplateStorageConfig.DefaultRootPath;
        var documentRoot = configuration[DocumentStorageConfig.RootPathKey]
            ?? DocumentStorageConfig.DefaultRootPath;

        services.AddSingleton<ITemplateFileStore>(_ => new FileSystemTemplateFileStore(templateRoot));
        services.AddSingleton<IDocumentFileStore>(_ => new FileSystemDocumentFileStore(documentRoot));

        // Application services are registered from here rather than from an AddApplication()
        // in Dms.Application itself: that project references Domain and nothing else — no
        // Microsoft.Extensions.DependencyInjection — and adding a package there to host three
        // lines of registration wasn't worth breaking that.
        services.AddScoped<TemplateRegistrationService>();
        services.AddScoped<DocumentTypeService>();
        services.AddScoped<OrganisationService>();
        services.AddScoped<DraftCreationService>();

        return services;
    }
}
