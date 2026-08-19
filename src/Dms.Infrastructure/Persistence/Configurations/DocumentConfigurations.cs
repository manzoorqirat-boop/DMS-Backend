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

        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasIndex(x => x.IsActive);
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
        builder.Property(x => x.ValidationIssues)
            .HasConversion(
                v => JsonSerializer.Serialize(v),
                v => (IReadOnlyList<string>)(JsonSerializer.Deserialize<List<string>>(v) ?? []))
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
        builder.HasIndex(x => new { x.DocumentTypeId, x.TemplateVersion }).IsUnique();

        builder.HasIndex(x => new { x.DocumentTypeId, x.Status });
    }
}
