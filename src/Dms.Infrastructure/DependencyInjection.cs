using Dms.Application.Abstractions;
using Dms.Application.DocumentTypes;
using Dms.Application.Distribution;
using Dms.Application.Documents;
using Dms.Application.Editing;
using Dms.Application.Notifications;
using Dms.Application.Access;
using Dms.Application.Metadata;
using Dms.Application.Numbering;
using Dms.Application.Signing;
using Dms.Application.Workflows;
using Dms.Application.Templates;
using Dms.Infrastructure.Persistence;
using Dms.Infrastructure.Persistence.Repositories;
using Dms.Infrastructure.Editing;
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
        var connectionString = DatabaseConnectionStringResolver.Resolve(
            Environment.GetEnvironmentVariable(DatabaseConnectionStringResolver.DatabaseUrlEnvironmentVariable),
            configuration.GetConnectionString("Postgres"));

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
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<ISignatureRepository, SignatureRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<INumberingRuleRepository, NumberingRuleRepository>();
        services.AddScoped<IAccessControl, AccessControl>();
        services.AddScoped<IWorkflowDefinitionRepository, WorkflowDefinitionRepository>();
        services.AddScoped<IMetadataFieldRepository, MetadataFieldRepository>();
        services.AddScoped<IReviewPolicyRepository, ReviewPolicyRepository>();
        services.AddScoped<IRetentionPolicyRepository, RetentionPolicyRepository>();
        services.AddScoped<IEditingSessionRepository, EditingSessionRepository>();
        services.AddSingleton<IEditorSettings, EditorSettings>();
        services.AddScoped<IDistributionRepository, DistributionRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IJobRunRepository, JobRunRepository>();
        services.AddScoped<INotificationRuleRepository, NotificationRuleRepository>();
        services.AddSingleton<IClock, SystemClock>();

        // Stand-in: logs instead of mailing. Replace before go-live — see LoggingNotificationSender.
        services.AddScoped<INotificationSender, LoggingNotificationSender>();

        // Stand-in: returns files unstamped and says so. Replace before any real controlled
        // copy is printed — see PassThroughPrintRenderer.
        services.AddSingleton<IControlledPrintRenderer, PassThroughPrintRenderer>();

        var maxAttempts = int.TryParse(configuration[$"{SigningPolicy.SectionName}:MaxFailedAttempts"], out var parsed)
            ? parsed
            : SigningPolicy.DefaultMaxFailedAttempts;
        var lockout = TimeSpan.TryParse(configuration[$"{SigningPolicy.SectionName}:LockoutDuration"], out var parsedSpan)
            ? parsedSpan
            : SigningPolicy.DefaultLockoutDuration;

        services.AddSingleton<ISigningPolicy>(new SigningPolicy(maxAttempts, lockout));

        // One instance serving both interfaces: recording and querying share the same
        // DbContext, and registering them separately would put audit writes in a different
        // change tracker from the change they describe.
        services.AddScoped<AuditTrail>();
        services.AddScoped<IAuditTrail>(sp => sp.GetRequiredService<AuditTrail>());
        services.AddScoped<IAuditQuery>(sp => sp.GetRequiredService<AuditTrail>());

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
        services.AddScoped<ReviewWorkflowService>();
        services.AddScoped<UserService>();
        services.AddScoped<RoleService>();
        services.AddScoped<NumberingRuleService>();
        services.AddScoped<WorkflowDefinitionService>();
        services.AddScoped<MetadataFieldService>();
        services.AddScoped<DocumentRevisionService>();
        services.AddScoped<DocumentLifecycleService>();
        services.AddScoped<RetentionService>();

        // The whole in-browser editing stack registers together or not at all. EditingService
        // depends on IEditorTokenService and IEditorContentFetcher, so registering it outside
        // this block left the container unresolvable on any deployment without a document
        // server configured — which is every deployment right now, including Railway. That
        // surfaced as a DI validation failure the first time anything validated the container
        // (`dotnet ef`), and would otherwise have waited to become a runtime 500 the first
        // time someone opened a document for editing.
        //
        // HmacEditorTokenService also throws on a missing secret by design, which is a second
        // reason none of this can be registered speculatively.
        if (!string.IsNullOrWhiteSpace(configuration[EditorConfig.UrlKey]))
        {
            services.AddSingleton<IEditorTokenService, HmacEditorTokenService>();
            services.AddHttpClient(HttpEditorContentFetcher.ClientName,
                client => client.Timeout = TimeSpan.FromMinutes(2));
            services.AddScoped<IEditorContentFetcher, HttpEditorContentFetcher>();
            services.AddScoped<EditingService>();
        }

        services.AddScoped<DistributionService>();
        services.AddScoped<NotificationService>();
        services.AddScoped<ReminderJob>();
        services.AddScoped<NotificationRuleService>();

        return services;
    }
}
