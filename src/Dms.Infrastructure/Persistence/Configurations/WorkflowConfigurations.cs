using Dms.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dms.Infrastructure.Persistence.Configurations;

public class WorkflowDefinitionConfiguration : IEntityTypeConfiguration<WorkflowDefinition>
{
    public void Configure(EntityTypeBuilder<WorkflowDefinition> builder)
    {
        builder.ToTable("workflow_definitions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.Name).HasMaxLength(128).IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(128).IsRequired();

        builder.HasOne<DocumentType>().WithMany().HasForeignKey(x => x.DocumentTypeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);

        // Steps is exposed as a computed ordered IReadOnlyList over a backing field, so EF has
        // to be pointed at the field rather than the property.
        builder.Metadata
            .FindNavigation(nameof(WorkflowDefinition.Steps))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(x => x.Steps)
            .WithOne()
            .HasForeignKey(x => x.WorkflowDefinitionId)
            .OnDelete(DeleteBehavior.Cascade);

        // At most one ACTIVE definition per (type, site). A partial unique index, same pattern
        // as one-active-template-per-type: the service deactivates before activating, but
        // read-then-write across two admins isn't atomic and this is what makes the loser fail
        // loudly rather than leaving two live routes with resolution by insertion order.
        builder.HasIndex(x => new { x.DocumentTypeId, x.SiteId })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasFilter("is_active = true")
            .HasDatabaseName("ux_workflow_definitions_one_active_per_scope");
    }
}

public class WorkflowStepDefinitionConfiguration : IEntityTypeConfiguration<WorkflowStepDefinition>
{
    public void Configure(EntityTypeBuilder<WorkflowStepDefinition> builder)
    {
        builder.ToTable("workflow_step_definitions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.StepLabel).HasMaxLength(128).IsRequired();

        builder.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.WorkflowDefinitionId, x.StepOrder })
            .IsUnique()
            .HasDatabaseName("ux_workflow_step_definitions_order");
    }
}
