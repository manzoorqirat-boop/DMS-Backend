using Dms.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dms.Infrastructure.Persistence.Configurations;

/// <summary>
/// <b>Reconstructed file.</b> Present in the working codebase before this review but absent
/// from the uploaded archive. Table and index-naming conventions below follow every sibling
/// configuration in this folder exactly (plain English plural table names, <c>ux_</c>/<c>ix_</c>
/// prefixes). The unique index name on <see cref="DmsUser.UserName"/> is fixed by a caller: <c>
/// UserService.CreateAsync</c> checks <c>outcome.ViolatedIndexContains("user_name")</c>, so
/// renaming it would silently break that conflict message.
/// </summary>
public class DmsUserConfiguration : IEntityTypeConfiguration<DmsUser>
{
    public void Configure(EntityTypeBuilder<DmsUser> builder)
    {
        builder.ToTable("users");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.UserName).HasMaxLength(128).IsRequired();
        builder.Property(x => x.FullName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Department).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Designation).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(320);
        builder.Property(x => x.PasswordHash).HasMaxLength(256).IsRequired();

        builder.HasIndex(x => x.UserName).IsUnique().HasDatabaseName("ux_users_user_name");
    }
}

public class SignatureRequestConfiguration : IEntityTypeConfiguration<SignatureRequest>
{
    public void Configure(EntityTypeBuilder<SignatureRequest> builder)
    {
        builder.ToTable("signature_requests");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.UserName).HasMaxLength(128).IsRequired();
        builder.Property(x => x.StepLabel).HasMaxLength(128).IsRequired();

        builder.HasOne<ControlledDocument>()
            .WithMany()
            .HasForeignKey(x => x.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<DmsUser>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.DocumentId, x.StepOrder })
            .IsUnique()
            .HasDatabaseName("ux_signature_requests_document_step");

        // Drives GetPendingForUserAsync and the reminder sweep's ListPendingForAllAsync — both
        // filter on Status, and the latter also orders by (DocumentId, StepOrder).
        builder.HasIndex(x => new { x.UserId, x.Status })
            .HasDatabaseName("ix_signature_requests_user_status");

        builder.HasIndex(x => x.Status)
            .HasDatabaseName("ix_signature_requests_status");
    }
}

public class ElectronicSignatureConfiguration : IEntityTypeConfiguration<ElectronicSignature>
{
    public void Configure(EntityTypeBuilder<ElectronicSignature> builder)
    {
        builder.ToTable("electronic_signatures");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.UserName).HasMaxLength(128).IsRequired();
        builder.Property(x => x.FullName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Department).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Designation).HasMaxLength(200).IsRequired();

        // A hex SHA-256 digest is exactly 64 characters; fixed-length keeps the column honest
        // about what it holds rather than accepting an arbitrary string.
        builder.Property(x => x.ContentHash).HasMaxLength(64).IsFixedLength().IsRequired();

        builder.Property(x => x.Reason).HasMaxLength(2000);

        builder.HasOne<ControlledDocument>()
            .WithMany()
            .HasForeignKey(x => x.DocumentId)
            .OnDelete(DeleteBehavior.Restrict);

        // Restrict, not Cascade: a SignatureRequest must never be deletable out from under an
        // applied signature — the whole record of who signed what would go with it.
        builder.HasOne<SignatureRequest>()
            .WithMany()
            .HasForeignKey(x => x.SignatureRequestId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<DmsUser>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        // At most one signature per step. Enforces at the database what
        // SignatureRequest.RequirePending already enforces in memory — the pairing this
        // codebase uses throughout for its single-transition invariants.
        builder.HasIndex(x => x.SignatureRequestId)
            .IsUnique()
            .HasDatabaseName("ux_electronic_signatures_request");

        builder.HasIndex(x => new { x.DocumentId, x.SignedAt })
            .HasDatabaseName("ix_electronic_signatures_document_signed");
    }
}
