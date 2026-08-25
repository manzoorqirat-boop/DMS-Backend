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
        builder.Property(x => x.PeriodKey).HasMaxLength(16).IsRequired();

        // PeriodKey is part of the key, not an attribute: a pattern containing {YYYY} gets one
        // counter per year, and those counters are genuinely different sequences. It is also
        // part of the ON CONFLICT target in AllocateNextSequenceAsync, so this index must match
        // that clause exactly or numbering stops working outright.
        builder.HasIndex(x => new { x.SiteId, x.DepartmentId, x.DocumentTypeId, x.PeriodKey })
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

        // Number plus revision, not number alone: every revision of a controlled document
        // keeps the same number, and that is the point of a document number.
        builder.HasIndex(x => new { x.DocumentNumber, x.Revision })
            .IsUnique()
            .HasDatabaseName("ux_controlled_documents_number_revision");

        // One row per revision within a lineage.
        builder.HasIndex(x => new { x.FamilyId, x.Revision })
            .IsUnique()
            .HasDatabaseName("ux_controlled_documents_family_revision");

        // At most one current revision per lineage. Partial index, same pattern as the active
        // template and active workflow: issuing a successor stands the predecessor down and
        // promotes the successor in one transaction, but read-then-write across two concurrent
        // issuances isn't atomic and this is what makes the loser fail loudly.
        builder.HasIndex(x => x.FamilyId)
            .IsUnique()
            .HasFilter("is_current_revision = true")
            .HasDatabaseName("ux_controlled_documents_one_current_per_family");

        // URS Functions #1 — title uniqueness scoped per document type, but only across
        // *current* revisions. A superseded Rev 00 and a live Rev 01 legitimately share a
        // title; without the filter, the first revision of anything would collide with its own
        // predecessor and the revision cycle simply wouldn't work.
        builder.HasIndex(x => new { x.DocumentTypeId, x.Title })
            .IsUnique()
            .HasFilter("is_current_revision = true")
            .HasDatabaseName("ux_controlled_documents_type_title_current");

        builder.HasIndex(x => x.IsCurrentRevision)
            .HasDatabaseName("ix_controlled_documents_current");

        builder.Property(x => x.ObsoleteReason).HasMaxLength(1000);
        builder.Property(x => x.LastReviewedBy).HasMaxLength(128);

        // Drives the periodic-review report. Partial, because only Effective documents can be
        // due — indexing the nulls on every draft and superseded revision would be dead weight.
        builder.HasIndex(x => x.NextReviewDate)
            .HasFilter("next_review_date IS NOT NULL")
            .HasDatabaseName("ix_controlled_documents_next_review");

        builder.HasIndex(x => new { x.DepartmentId, x.Status })
            .HasDatabaseName("ix_controlled_documents_department_status");
    }
}
