using Dms.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dms.Infrastructure.Persistence.Configurations;

public class DocumentDistributionConfiguration : IEntityTypeConfiguration<DocumentDistribution>
{
    public void Configure(EntityTypeBuilder<DocumentDistribution> builder)
    {
        builder.ToTable("document_distributions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.IssuedToName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.IssuedBy).HasMaxLength(128).IsRequired();
        builder.Property(x => x.AcknowledgedBy).HasMaxLength(128);
        builder.Property(x => x.ReturnedBy).HasMaxLength(128);
        builder.Property(x => x.ClosureNote).HasMaxLength(1000);

        builder.HasOne<ControlledDocument>()
            .WithMany()
            .HasForeignKey(x => x.DocumentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Department>()
            .WithMany()
            .HasForeignKey(x => x.IssuedToDepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        // Copy numbers are unique per document and never reused — a retrieval checklist ticks
        // against them, so "copy 3" must mean exactly one thing for the life of the document.
        builder.HasIndex(x => new { x.DocumentId, x.CopyNumber })
            .IsUnique()
            .HasDatabaseName("ux_document_distributions_copy_number");

        // Drives the retrieval worklist.
        builder.HasIndex(x => x.Status)
            .HasDatabaseName("ix_document_distributions_status");
    }
}

public class PrintEventConfiguration : IEntityTypeConfiguration<PrintEvent>
{
    public void Configure(EntityTypeBuilder<PrintEvent> builder)
    {
        builder.ToTable("print_events");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.PrintedBy).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Watermark).HasMaxLength(500).IsRequired();

        builder.HasOne<DocumentDistribution>()
            .WithMany()
            .HasForeignKey(x => x.DistributionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.DocumentId, x.PrintedAt })
            .HasDatabaseName("ix_print_events_document_printed");

        builder.HasIndex(x => new { x.DistributionId, x.PrintSequence })
            .IsUnique()
            .HasDatabaseName("ux_print_events_distribution_sequence");
    }
}
