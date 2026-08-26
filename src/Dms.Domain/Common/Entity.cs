namespace Dms.Domain.Common;

/// <summary>
/// Base for all persisted entities.
/// <para>
/// Identifiers are <b>UUIDv7</b>, generated in the application rather than by the database —
/// same convention as ERES/Hastakshar: keys sort in creation order (good index locality,
/// unlike random UUIDv4), and an entity has a stable identity before it's ever saved, so an
/// audit record can reference the thing it describes inside the same transaction.
/// </para>
/// </summary>
public abstract class Entity
{
    /// <summary>Primary key. Assigned once, at construction. Never reassigned.</summary>
    public Guid Id { get; protected set; } = Uuid7.NewGuid();

    public override bool Equals(object? obj) =>
        obj is Entity other && GetType() == other.GetType() && Id.Equals(other.Id);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}

/// <summary>Created/updated stamps for mutable master-data entities.</summary>
public interface ITimestamped
{
    DateTimeOffset CreatedAt { get; }
    DateTimeOffset? UpdatedAt { get; }
}
