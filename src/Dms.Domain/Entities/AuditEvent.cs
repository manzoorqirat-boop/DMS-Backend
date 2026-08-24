using Dms.Domain.Common;
using Dms.Domain.Enums;

namespace Dms.Domain.Entities;

/// <summary>
/// One entry in the audit trail. 21 CFR Part 11 §11.10(e): secure, computer-generated,
/// time-stamped, attributable, and not obscuring previously recorded information.
/// <para>
/// <b>Append-only, by construction.</b> Every property is set once in the constructor and
/// there is no mutator, no setter, and deliberately no delete or archive method anywhere in
/// the codebase. That is the first of three layers: this entity offers no way to change an
/// event, <c>DmsDbContext.SaveChanges</c> rejects any attempt to modify or delete one even if
/// something bypasses the entity, and a database trigger blocks UPDATE and DELETE on the table
/// outright so that a direct psql session can't do it either. Application-level immutability
/// alone is not immutability — anyone with the connection string can still rewrite history.
/// </para>
/// </summary>
public class AuditEvent : Entity
{
    private AuditEvent() { }

    public AuditEvent(
        AuditAction action,
        string entityType,
        Guid entityId,
        string entityLabel,
        string actor,
        string? details = null)
    {
        Action = action;
        EntityType = RequireNonEmpty(entityType, nameof(entityType));
        EntityId = entityId;
        EntityLabel = RequireNonEmpty(entityLabel, nameof(entityLabel));
        Actor = RequireNonEmpty(actor, nameof(actor));
        Details = details;
        OccurredAt = DateTimeOffset.UtcNow;
    }

    public AuditAction Action { get; private set; }

    /// <summary>Aggregate the event is about — "ControlledDocument", "DocumentTemplate", etc.</summary>
    public string EntityType { get; private set; } = "";

    public Guid EntityId { get; private set; }

    /// <summary>
    /// Human-readable identity of the subject at the time of the event — a document number, a
    /// template name and version, a site code. Denormalised on purpose: the trail has to stay
    /// readable on its own decades later, without joining to rows that may since have been
    /// renamed, and an audit entry that reads "record 0192f3a1-… was approved" is useless to
    /// the inspector actually holding the printout.
    /// </summary>
    public string EntityLabel { get; private set; } = "";

    /// <summary>Username of the person responsible. Never a service account for a user-initiated action.</summary>
    public string Actor { get; private set; } = "";

    /// <summary>Optional free-form context — a reason for change, or what moved from what to what.</summary>
    public string? Details { get; private set; }

    /// <summary>
    /// Server-generated UTC timestamp. Taken here rather than accepted from a caller: a
    /// timestamp a client can set is not a timestamp an auditor can rely on.
    /// </summary>
    public DateTimeOffset OccurredAt { get; private set; }

    private static string RequireNonEmpty(string value, string paramName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{paramName} is required.", paramName)
            : value;
}
