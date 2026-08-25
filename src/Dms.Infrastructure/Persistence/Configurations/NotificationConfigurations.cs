using Dms.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dms.Infrastructure.Persistence.Configurations;

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.RecipientUserName).HasMaxLength(128).IsRequired();
        builder.Property(x => x.RecipientEmail).HasMaxLength(320);
        builder.Property(x => x.Subject).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Body).HasMaxLength(4000).IsRequired();
        builder.Property(x => x.DedupeKey).HasMaxLength(200).IsRequired();
        builder.Property(x => x.FailureReason).HasMaxLength(1000);

        builder.HasOne<DmsUser>().WithMany().HasForeignKey(x => x.RecipientUserId).OnDelete(DeleteBehavior.Restrict);

        // The idempotency guarantee for the reminder sweep. The job checks keys in bulk first,
        // but that check is read-then-write; this index is what actually holds when two
        // application instances run the sweep in the same second.
        builder.HasIndex(x => x.DedupeKey)
            .IsUnique()
            .HasDatabaseName("ux_notifications_dedupe_key");

        builder.HasIndex(x => new { x.RecipientUserId, x.CreatedAt })
            .HasDatabaseName("ix_notifications_recipient_created");

        // Drives the dispatch queue. Partial, since Sent rows vastly outnumber Pending ones
        // within days of go-live and indexing them would be dead weight.
        builder.HasIndex(x => x.Status)
            .HasFilter("status = 'Pending'")
            .HasDatabaseName("ix_notifications_pending");
    }
}

public class ScheduledJobRunConfiguration : IEntityTypeConfiguration<ScheduledJobRun>
{
    public void Configure(EntityTypeBuilder<ScheduledJobRun> builder)
    {
        builder.ToTable("scheduled_job_runs");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.JobName).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Trigger).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Detail).HasMaxLength(4000);

        builder.HasIndex(x => new { x.JobName, x.StartedAt })
            .HasDatabaseName("ix_scheduled_job_runs_job_started");
    }
}

public class NotificationRuleConfiguration : IEntityTypeConfiguration<NotificationRule>
{
    public void Configure(EntityTypeBuilder<NotificationRule> builder)
    {
        builder.ToTable("notification_rules");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.SubjectTemplate).HasMaxLength(256).IsRequired();
        builder.Property(x => x.BodyTemplate).HasMaxLength(4000).IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(128).IsRequired();

        builder.HasOne<DocumentType>().WithMany().HasForeignKey(x => x.DocumentTypeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Role>().WithMany().HasForeignKey(x => x.RecipientRoleId).OnDelete(DeleteBehavior.Restrict);

        // One rule per (kind, document type), with the catch-all stored as a NULL type. NULLS
        // NOT DISTINCT, or a kind could accumulate several conflicting catch-alls and
        // resolution would depend on insertion order.
        builder.HasIndex(x => new { x.Kind, x.DocumentTypeId })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasDatabaseName("ux_notification_rules_scope");

        builder.HasIndex(x => new { x.Kind, x.IsEnabled })
            .HasDatabaseName("ix_notification_rules_kind_enabled");
    }
}
