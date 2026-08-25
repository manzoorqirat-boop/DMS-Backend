namespace Dms.Application.Common;

/// <summary>
/// What happened when a repository tried to flush. Unique-constraint collisions are an
/// expected outcome here, not a fault: the database indexes are what actually serialise two
/// concurrent uploads racing for the same template version, or two admins racing to activate
/// different templates for the same document type. The application service reads the
/// constraint name to tell those two races apart and return the right message.
/// <para>
/// Reported this way rather than by letting <c>DbUpdateException</c> escape, so the
/// Application layer never has to reference EF Core or Npgsql — see Dms.Application.csproj,
/// which references Domain and nothing else.
/// </para>
/// </summary>
public sealed record PersistOutcome(bool Saved, string? ViolatedConstraint, bool IsConcurrencyConflict = false)
{
    public static readonly PersistOutcome Success = new(true, null);

    public static PersistOutcome UniqueViolation(string? constraintName) => new(false, constraintName);

    /// <summary>
    /// Someone else changed the row between it being read and saved. Distinct from a unique
    /// violation because the caller's message differs: "reload, someone else edited this" is
    /// actionable, "that value is taken" is not.
    /// </summary>
    public static readonly PersistOutcome ConcurrencyConflict = new(false, null, true);

    /// <summary>
    /// Case-insensitive substring match — Postgres reports the index name, and callers care
    /// about which index, not its exact casing.
    /// </summary>
    public bool ViolatedIndexContains(string fragment) =>
        ViolatedConstraint is not null
        && ViolatedConstraint.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}
