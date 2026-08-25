using Dms.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dms.Infrastructure.Persistence.Configurations;

public class EditingSessionConfiguration : IEntityTypeConfiguration<EditingSession>
{
    public void Configure(EntityTypeBuilder<EditingSession> builder)
    {
        builder.ToTable("editing_sessions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.UserName).HasMaxLength(128).IsRequired();
        builder.Property(x => x.SessionKey).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ClosedBy).HasMaxLength(128);
        builder.Property(x => x.ClosureNote).HasMaxLength(500);

        builder.HasOne<ControlledDocument>()
            .WithMany()
            .HasForeignKey(x => x.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        // The check-out lock itself. Partial unique index rather than service-side checking,
        // because two people pressing Edit at the same moment would both see no active session
        // — this is what makes the loser fail instead of producing two live locks.
        builder.HasIndex(x => x.DocumentId)
            .IsUnique()
            .HasFilter("status = 'Active'")
            .HasDatabaseName("ux_editing_sessions_one_active_per_document");

        builder.HasIndex(x => x.SessionKey)
            .IsUnique()
            .HasDatabaseName("ux_editing_sessions_key");
    }
}
