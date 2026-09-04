using System.Text.Json;
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
        builder.Property(x => x.EmployeeId).HasMaxLength(64);

        // Stored as jsonb through an explicit conversion, for the same reason
        // DocumentTemplate.ValidationIssues is: an interface-typed collection is not something
        // to leave EF's primitive-collection inference to guess at. Block-bodied lambdas would
        // not compile here — HasConversion's two-lambda form is expression-tree only — so
        // every optional argument is supplied explicitly.
        builder.Property(x => x.PasswordHistory)
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => (IReadOnlyList<string>)(JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>()))
            .HasColumnType("jsonb")
            .IsRequired();

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


/// <summary>
/// Exactly one row is expected — the organisation's single password policy. Not enforced with
/// a check constraint because a second row would be harmless (the service reads the first and
/// seeds one if absent), and a constraint that can never legitimately fire is noise.
/// </summary>
public class PasswordPolicyConfiguration : IEntityTypeConfiguration<PasswordPolicy>
{
    public void Configure(EntityTypeBuilder<PasswordPolicy> builder)
    {
        builder.ToTable("password_policies");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.UpdatedBy).HasMaxLength(128).IsRequired();
    }
}


/// <summary>
/// One row, holding all seven status stamps as JSON. A table with a row per status would need
/// a schema migration to buy nothing — the set is fixed by the DocumentStatus enum, so it can
/// never grow an eighth member at runtime.
/// </summary>
public class DocumentStatusStampsConfiguration : IEntityTypeConfiguration<DocumentStatusStamps>
{
    public void Configure(EntityTypeBuilder<DocumentStatusStamps> builder)
    {
        builder.ToTable("document_status_stamps");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.StampsJson).HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.UpdatedBy).HasMaxLength(128).IsRequired();

        // Stamps is computed from StampsJson and has no setter EF could use.
        builder.Ignore(x => x.Stamps);
    }
}

/// <summary>One row holding every signature point as JSON — see SignaturePolicy.</summary>
public class SignaturePolicyConfiguration : IEntityTypeConfiguration<SignaturePolicy>
{
    public void Configure(EntityTypeBuilder<SignaturePolicy> builder)
    {
        builder.ToTable("signature_policies");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.PointsJson).HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.UpdatedBy).HasMaxLength(128).IsRequired();

        // Computed from PointsJson; no setter for EF to use.
        builder.Ignore(x => x.Points);
    }
}

public class PendingActionConfiguration : IEntityTypeConfiguration<PendingAction>
{
    public void Configure(EntityTypeBuilder<PendingAction> builder)
    {
        builder.ToTable("pending_actions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.Action).HasConversion<string>().HasMaxLength(64).IsRequired();
        builder.Property(x => x.Timing).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.CountersignerPermission).HasConversion<string>().HasMaxLength(64);

        builder.Property(x => x.SubjectType).HasMaxLength(64).IsRequired();
        builder.Property(x => x.SubjectLabel).HasMaxLength(256).IsRequired();
        builder.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.RejectionReason).HasMaxLength(1024);

        // The countersignature worklist's own query: everything still awaiting someone, oldest
        // first. Indexed because it is read on every page load of that screen.
        builder.HasIndex(x => new { x.Status, x.CreatedAt })
            .HasDatabaseName("ix_pending_actions_status_created");

        builder.HasIndex(x => new { x.SubjectType, x.SubjectId })
            .HasDatabaseName("ix_pending_actions_subject");

        // Signatures are owned by the action and loaded with it — a pending action without its
        // signatures cannot answer who has already signed, which is the first thing every
        // caller needs.
        builder.HasMany(x => x.Signatures)
            .WithOne()
            .HasForeignKey(x => x.PendingActionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Signatures is an expression-bodied IReadOnlyList over a private list, so EF has to be
        // told to write through the field rather than looking for a setter it will not find.
        builder.Navigation(x => x.Signatures)
            .HasField("_signatures")
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .AutoInclude();
    }
}

/// <summary>
/// Append-only, like ElectronicSignature. The trigger that enforces this in the database lives
/// in AuditImmutability.sql — a signature that could be edited afterwards would not be one.
/// </summary>
public class ActionSignatureConfiguration : IEntityTypeConfiguration<ActionSignature>
{
    public void Configure(EntityTypeBuilder<ActionSignature> builder)
    {
        builder.ToTable("action_signatures");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.Meaning).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.UserName).HasMaxLength(128).IsRequired();
        builder.Property(x => x.FullName).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Department).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Designation).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(1024);

        builder.Ignore(x => x.UpdatedAt);
    }
}
