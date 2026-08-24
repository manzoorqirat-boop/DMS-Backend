using Dms.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dms.Infrastructure.Persistence.Configurations;

public class MetadataFieldDefinitionConfiguration : IEntityTypeConfiguration<MetadataFieldDefinition>
{
    public void Configure(EntityTypeBuilder<MetadataFieldDefinition> builder)
    {
        builder.ToTable("metadata_field_definitions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.Tag).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Label).HasMaxLength(128).IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(128).IsRequired();

        builder.HasOne<DocumentType>()
            .WithMany()
            .HasForeignKey(x => x.DocumentTypeId)
            .OnDelete(DeleteBehavior.Cascade);

        // One definition per tag per type. MetadataResolver assigns into a dictionary keyed by
        // tag, so a duplicate would silently shadow rather than error — this constraint is what
        // stops that being possible in the first place.
        builder.HasIndex(x => new { x.DocumentTypeId, x.Tag })
            .IsUnique()
            .HasDatabaseName("ux_metadata_field_definitions_type_tag");
    }
}
