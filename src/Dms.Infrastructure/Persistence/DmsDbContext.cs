using System.Text;
using Dms.Domain.Entities;
using Dms.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Dms.Infrastructure.Persistence;

public class DmsDbContext(DbContextOptions<DmsDbContext> options) : DbContext(options)
{
    public const string Schema = "dms";

    public DbSet<DocumentType> DocumentTypes => Set<DocumentType>();
    public DbSet<DocumentTemplate> DocumentTemplates => Set<DocumentTemplate>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<ControlledDocument> ControlledDocuments => Set<ControlledDocument>();
    public DbSet<DocumentNumberSequence> DocumentNumberSequences => Set<DocumentNumberSequence>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<DmsUser> Users => Set<DmsUser>();
    public DbSet<SignatureRequest> SignatureRequests => Set<SignatureRequest>();
    public DbSet<ElectronicSignature> ElectronicSignatures => Set<ElectronicSignature>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRoleAssignment> UserRoleAssignments => Set<UserRoleAssignment>();
    public DbSet<NumberingRule> NumberingRules => Set<NumberingRule>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // Enums are persisted as their NAME, not their ordinal — same convention as ERES's
        // EresDbContext, and the same reasoning: an inspector or DBA reading the
        // document_templates table directly must see 'Active', not '3', and a future enum
        // reorder must not be a silent data-corruption event.
        builder.Properties<TemplateStatus>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<DocumentStatus>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<AuditAction>().HaveConversion<string>().HaveMaxLength(64);
        builder.Properties<Permission>().HaveConversion<string>().HaveMaxLength(64);
        builder.Properties<SignatureRole>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<SignatureRequestStatus>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<SignatureMeaning>().HaveConversion<string>().HaveMaxLength(32);

        base.ConfigureConventions(builder);
    }

    /// <summary>
    /// Rejects any attempt to modify or delete an <see cref="AuditEvent"/>.
    /// <para>
    /// The second of three layers protecting the trail. The entity itself exposes no mutators,
    /// but reflection, a future <c>ExecuteUpdate</c>, or simply someone adding a setter would
    /// all get past that; this catches them at the point of writing. The third layer — a
    /// database trigger blocking UPDATE and DELETE on the table — is the one that also stops a
    /// direct psql session, and is applied by migration (see <c>Migrations/AuditImmutability.sql</c>).
    /// </para>
    /// <para>
    /// Throwing rather than silently discarding the change is deliberate: a caller that tried
    /// to rewrite an audit record has a bug or worse, and the request should fail loudly.
    /// </para>
    /// </summary>
    private void GuardAppendOnlyEntities()
    {
        GuardAppendOnly<AuditEvent>("Audit events");

        // Applied signatures are append-only for the same reason and with more at stake: an
        // amendable signature is not a signature. A decision that turns out to be wrong is
        // corrected by a new signature on a new revision, never by editing the old one.
        GuardAppendOnly<ElectronicSignature>("Electronic signatures");
    }

    private void GuardAppendOnly<T>(string description) where T : class
    {
        var violations = ChangeTracker.Entries<T>()
            .Where(entry => entry.State is EntityState.Modified or EntityState.Deleted)
            .ToList();

        if (violations.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{description} are append-only; {violations.Count} entr(y/ies) were marked "
            + $"{string.Join(", ", violations.Select(v => v.State).Distinct())}.");
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardAppendOnlyEntities();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        GuardAppendOnlyEntities();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DmsDbContext).Assembly);

        ApplySnakeCaseNaming(modelBuilder);

        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Rewrites every table, column, key, index and foreign-key name to snake_case. Copied
    /// verbatim from ERES's EresDbContext rather than shared via a package — see that file
    /// for why: keeping each regulated repo's dependency surface minimal beat deduplicating
    /// thirty lines across two otherwise-independent codebases.
    /// </summary>
    private static void ApplySnakeCaseNaming(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            var tableName = entity.GetTableName();
            if (tableName is not null)
            {
                entity.SetTableName(ToSnakeCase(tableName));
            }

            var storeObject = StoreObjectIdentifier.Create(entity, StoreObjectType.Table);

            foreach (var property in entity.GetProperties())
            {
                var columnName = storeObject is { } target
                    ? property.GetColumnName(target) ?? property.Name
                    : property.Name;

                property.SetColumnName(ToSnakeCase(columnName));
            }

            foreach (var key in entity.GetKeys())
            {
                var name = key.GetName();
                if (name is not null)
                {
                    key.SetName(ToSnakeCase(name));
                }
            }

            foreach (var fk in entity.GetForeignKeys())
            {
                var name = fk.GetConstraintName();
                if (name is not null)
                {
                    fk.SetConstraintName(ToSnakeCase(name));
                }
            }

            foreach (var index in entity.GetIndexes())
            {
                var name = index.GetDatabaseName();
                if (name is not null)
                {
                    index.SetDatabaseName(ToSnakeCase(name));
                }
            }
        }
    }

    private static string ToSnakeCase(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var builder = new StringBuilder(input.Length + 8);

        for (var i = 0; i < input.Length; i++)
        {
            var current = input[i];

            if (char.IsUpper(current))
            {
                var previous = i > 0 ? input[i - 1] : '\0';
                var next = i + 1 < input.Length ? input[i + 1] : '\0';

                // Insert a separator at a lower→upper boundary (Ip|Address) or at the end
                // of an acronym run (HTTP|Server), but never doubling an existing one.
                var boundary = previous is not ('\0' or '_')
                    && (!char.IsUpper(previous) || (char.IsUpper(previous) && char.IsLower(next)));

                if (boundary)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(current));
            }
            else
            {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }
}
