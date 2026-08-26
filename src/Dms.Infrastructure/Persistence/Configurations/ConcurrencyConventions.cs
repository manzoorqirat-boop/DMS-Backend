using Dms.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Dms.Infrastructure.Persistence.Configurations;

/// <summary>
/// Optimistic concurrency for configuration and master data, using Postgres's own
/// <c>xmin</c> system column.
/// <para>
/// Chosen over an explicit rowversion column because it needs no schema change, no application
/// code to maintain, and cannot be forgotten on an insert. Postgres updates it on every row
/// version automatically.
/// </para>
/// <para>
/// Applied to the entities two administrators might realistically edit at the same moment.
/// Without it the second save silently overwrites the first, and the audit trail shows both
/// changes as though both took effect — which is worse than the lost update itself, because it
/// makes the trail wrong rather than merely incomplete.
/// </para>
/// <para>
/// Deliberately <b>not</b> applied to append-only tables (audit events, signatures, print
/// events) — nothing updates those, so a concurrency token would be dead weight — nor to rows
/// whose contention is already handled by a database constraint, such as the number-sequence
/// counter, whose UPSERT takes a row lock instead.
/// </para>
/// <para>
/// Configured through EF Core's own standard <c>IsRowVersion()</c> on a shadow <c>uint</c>
/// property named <c>xmin</c>, rather than the Npgsql-provider-specific
/// <c>UseXminAsConcurrencyToken()</c> helper this originally used — that helper was marked
/// obsolete in favour of exactly this pattern, and Npgsql's provider still recognises a
/// property literally named "xmin" as the Postgres system column either way, so the effect is
/// identical.
/// </para>
/// </summary>
public static class ConcurrencyConventions
{
    public static void ApplyConcurrencyTokens(this ModelBuilder modelBuilder)
    {
        Type[] contended =
        [
            // Configuration an admin edits.
            typeof(Role),
            typeof(WorkflowDefinition),
            typeof(NumberingRule),
            typeof(ReviewPolicy),
            typeof(RetentionPolicy),
            typeof(MetadataFieldDefinition),
            typeof(NotificationRule),

            // Master data.
            typeof(DocumentType),
            typeof(Site),
            typeof(Department),
            typeof(DmsUser),

            // Lifecycle records with several actors: an author editing, a reviewer signing and
            // an administrator issuing can all touch the same document.
            typeof(ControlledDocument),
            typeof(DocumentTemplate),
            typeof(DocumentDistribution),
            typeof(EditingSession),
            typeof(SignatureRequest),
        ];

        foreach (var type in contended)
        {
            modelBuilder.Entity(type).Property<uint>("xmin").IsRowVersion();
        }
    }
}
