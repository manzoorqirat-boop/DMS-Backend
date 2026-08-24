using Dms.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dms.Infrastructure.Persistence.Configurations;

public class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("audit_events");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.EntityType).HasMaxLength(64).IsRequired();
        builder.Property(x => x.EntityLabel).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Actor).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Details).HasMaxLength(2000);

        // No foreign key to the subject, on purpose. An audit event must outlive whatever it
        // describes and must never be the reason a delete is blocked or, worse, be cascaded
        // away with it. EntityLabel is what keeps the record readable without the join.
        builder.HasIndex(x => new { x.EntityId, x.OccurredAt })
            .HasDatabaseName("ix_audit_events_entity_occurred");

        builder.HasIndex(x => x.OccurredAt)
            .HasDatabaseName("ix_audit_events_occurred");

        builder.HasIndex(x => x.Actor)
            .HasDatabaseName("ix_audit_events_actor");
    }
}
