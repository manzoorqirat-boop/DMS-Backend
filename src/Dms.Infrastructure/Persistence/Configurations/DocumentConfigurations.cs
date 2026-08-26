using System.Text.Json;
using Dms.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dms.Infrastructure.Persistence.Configurations;

public class DocumentTypeConfiguration : IEntityTypeConfiguration<DocumentType>
{
    public void Configure(EntityTypeBuilder<DocumentType> builder)
    {
        builder.ToTable("document_types");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.Code).HasMaxLength(16).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();

        // Named explicitly rather than left to EF's IX_ convention: the application service
        // identifies which constraint a failed insert violated by name, so these names are
        // part of the contract between layers and shouldn't drift with a property rename.
        builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("ux_document_types_code");
        builder.HasIndex(x => x.IsActive).HasDatabaseName("ix_document_types_is_active");
    }
}

public class DocumentTemplateConfiguration : IEntityTypeConfiguration<DocumentTemplate>
{
    public void Configure(EntityTypeBuilder<DocumentTemplate> builder)
    {
        builder.ToTable("document_templates");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.StorageKey).HasMaxLength(500).IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(128).IsRequired();

        // Stored as jsonb via an explicit conversion rather than relying on EF's primitive-
        // collection inference to guess right on an interface-typed (IReadOnlyList<string>)
        // property — pinning the mapping down explicitly beats trusting convention here.
        //
        // Block-bodied lambdas here, deliberately: EF's HasConversion(Func, Func) overload and
        // its HasConversion(Expression<Func<...>>, Expression<Func<...>>) overload both exist,
        // and a single-expression lambda is ambiguous enough between them that the compiler
        // picked the expression-tree one — which then rejects JsonSerializer.Serialize's
        // optional `options` parameter and the `?? []` collection expression outright, neither
        // of which an expression tree can contain. A block body can only ever bind to a real
        // delegate, never an expression tree, which removes the ambiguity entirely rather than
        // working around each symptom one compiler error at a time.
        builder.Property(x => x.ValidationIssues)
            .HasConversion(
                v =>
                {
                    return JsonSerializer.Serialize(v);
                },
                v =>
                {
                    return (IReadOnlyList<string>)(JsonSerializer.Deserialize<List<string>>(v) ?? new List<string>());
                })
            .HasColumnType("jsonb")
            .IsRequired();

        builder.HasOne<DocumentType>()
            .WithMany()
            .HasForeignKey(x => x.DocumentTypeId)
            // Restrict, not Cascade: a DocumentType is deactivated, never deleted, once a
            // template references it — same reasoning as ERES's Site/Department links.
            .OnDelete(DeleteBehavior.Restrict);

        // The application service assigns TemplateVersion sequentially per DocumentType, but
        // this constraint is what actually stops two concurrent uploads racing to the same
        // number.
        builder.HasIndex(x => new { x.DocumentTypeId, x.TemplateVersion })
            .IsUnique()
            .HasDatabaseName("ux_document_templates_type_version");

        // Partial unique index — the real enforcement of "at most one Active template per
        // DocumentType". The activation service retires the incumbent before promoting the
        // successor, but read-then-write is not atomic across two concurrent admins; this
        // index is what makes the loser fail loudly instead of leaving the type with two live
        // templates. Filter is raw SQL, so it names the snake_case column and the enum's
        // persisted name-string, matching the HaveConversion<string>() convention in
        // DmsDbContext.ConfigureConventions.
        builder.HasIndex(x => x.DocumentTypeId)
            .IsUnique()
            .HasFilter("status = 'Active'")
            .HasDatabaseName("ux_document_templates_one_active_per_type");

        builder.HasIndex(x => new { x.DocumentTypeId, x.Status })
            .HasDatabaseName("ix_document_templates_type_status");
    }
}
