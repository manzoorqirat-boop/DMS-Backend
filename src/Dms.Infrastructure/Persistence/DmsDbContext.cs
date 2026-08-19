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

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // Enums are persisted as their NAME, not their ordinal — same convention as ERES's
        // EresDbContext, and the same reasoning: an inspector or DBA reading the
        // document_templates table directly must see 'Active', not '3', and a future enum
        // reorder must not be a silent data-corruption event.
        builder.Properties<TemplateStatus>().HaveConversion<string>().HaveMaxLength(32);

        base.ConfigureConventions(builder);
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
