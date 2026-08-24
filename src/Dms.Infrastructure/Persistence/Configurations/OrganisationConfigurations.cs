using Dms.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dms.Infrastructure.Persistence.Configurations;

public class SiteConfiguration : IEntityTypeConfiguration<Site>
{
    public void Configure(EntityTypeBuilder<Site> builder)
    {
        builder.ToTable("sites");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.Code).HasMaxLength(16).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();

        builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("ux_sites_code");
    }
}

public class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> builder)
    {
        builder.ToTable("departments");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.Code).HasMaxLength(16).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();

        builder.HasOne<Site>()
            .WithMany()
            .HasForeignKey(x => x.SiteId)
            .OnDelete(DeleteBehavior.Restrict);

        // Scoped to the site, not global: two sites may each have a QA department and they
        // are not the same department.
        builder.HasIndex(x => new { x.SiteId, x.Code })
            .IsUnique()
            .HasDatabaseName("ux_departments_site_code");
    }
}

public class DocumentNumberSequenceConfiguration : IEntityTypeConfiguration<DocumentNumberSequence>
{
    public void Configure(EntityTypeBuilder<DocumentNumberSequence> builder)
    {
        builder.ToTable("document_number_sequences");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        // This unique index is not just an integrity rule — it's the conflict target of the
        // ON CONFLICT clause in ControlledDocumentRepository.AllocateNextSequenceAsync.
        // Postgres requires a matching unique index for that clause to be legal at all, so
        // removing or renaming this breaks numbering outright rather than degrading it.
        builder.HasIndex(x => new { x.SiteId, x.DepartmentId, x.DocumentTypeId })
            .IsUnique()
            .HasDatabaseName("ux_document_number_sequences_scope");
    }
}

public class ControlledDocumentConfiguration : IEntityTypeConfiguration<ControlledDocument>
{
    public void Configure(EntityTypeBuilder<ControlledDocument> builder)
    {
        builder.ToTable("controlled_documents");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.DocumentNumber).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Title).HasMaxLength(400).IsRequired();
        builder.Property(x => x.WorkingCopyKey).HasMaxLength(500).IsRequired();
        builder.Property(x => x.Author).HasMaxLength(128).IsRequired();

        builder.HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DocumentType>().WithMany().HasForeignKey(x => x.DocumentTypeId).OnDelete(DeleteBehavior.Restrict);

        // Restrict on the template too: a template version must remain resolvable for as long
        // as any document created from it exists, which is what makes TemplateId a meaningful
        // pin rather than a dangling id.
        builder.HasOne<DocumentTemplate>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.DocumentNumber)
            .IsUnique()
            .HasDatabaseName("ux_controlled_documents_number");

        // URS Functions #1 — title uniqueness is scoped per document type. Enforced here
        // rather than only in the service, since two concurrent creations would otherwise
        // both pass a service-side existence check.
        builder.HasIndex(x => new { x.DocumentTypeId, x.Title })
            .IsUnique()
            .HasDatabaseName("ux_controlled_documents_type_title");

        builder.HasIndex(x => new { x.DepartmentId, x.Status })
            .HasDatabaseName("ix_controlled_documents_department_status");
    }
}
