using Dms.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dms.Infrastructure.Persistence.Configurations;

public class ReviewPolicyConfiguration : IEntityTypeConfiguration<ReviewPolicy>
{
    public void Configure(EntityTypeBuilder<ReviewPolicy> builder)
    {
        builder.ToTable("review_policies");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.CreatedBy).HasMaxLength(128).IsRequired();

        builder.HasOne<DocumentType>().WithMany().HasForeignKey(x => x.DocumentTypeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);

        // One policy per (type, site), the site-wide default stored as a NULL site. NULLS NOT
        // DISTINCT, or a type could accumulate several conflicting defaults and resolution
        // would depend on insertion order.
        builder.HasIndex(x => new { x.DocumentTypeId, x.SiteId })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasDatabaseName("ux_review_policies_scope");
    }
}

public class RetentionPolicyConfiguration : IEntityTypeConfiguration<RetentionPolicy>
{
    public void Configure(EntityTypeBuilder<RetentionPolicy> builder)
    {
        builder.ToTable("retention_policies");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.CreatedBy).HasMaxLength(128).IsRequired();

        builder.HasOne<DocumentType>().WithMany().HasForeignKey(x => x.DocumentTypeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);

        // Keyed on trigger too: a type can legitimately have one schedule running from
        // supersession and another from obsolescence, and they are different policies rather
        // than competing versions of one.
        builder.HasIndex(x => new { x.DocumentTypeId, x.SiteId, x.Trigger })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasDatabaseName("ux_retention_policies_scope");
    }
}
