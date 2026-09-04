using Dms.Application.Common;
using Dms.Domain.Entities;

namespace Dms.Application.Abstractions;

/// <summary>
/// Reads and writes the single password policy row.
/// <para>
/// <see cref="GetAsync"/> never returns null: an installation with no policy row yet must
/// still enforce sensible rules rather than none at all, so the implementation seeds the
/// defaults on first read. A missing policy quietly meaning "no password rules" is precisely
/// the failure mode worth designing out.
/// </para>
/// </summary>
public interface IPasswordPolicyRepository
{
    Task<PasswordPolicy> GetAsync(CancellationToken cancellationToken);

    Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reads and writes the single status-stamp row, seeding defaults on first read for the same
/// reason <see cref="IPasswordPolicyRepository"/> does: an installation predating this feature
/// must start stamping at the next render, not only after a redeploy re-runs bootstrap.
/// </summary>
public interface IDocumentStatusStampsRepository
{
    Task<DocumentStatusStamps> GetAsync(CancellationToken cancellationToken);

    Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Reads and writes the single signature-policy row, seeding defaults on first read.</summary>
public interface ISignaturePolicyRepository
{
    Task<SignaturePolicy> GetAsync(CancellationToken cancellationToken);

    Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The countersignature queue.
/// </summary>
public interface IPendingActionRepository
{
    Task<PendingAction?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Actions still awaiting a countersignature, oldest first — a queue, so the thing waiting
    /// longest is dealt with first rather than whatever happens to sort highest.
    /// </summary>
    Task<IReadOnlyList<PendingAction>> ListAwaitingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Whether this subject already has an action awaiting countersignature. Guards against a
    /// second close-out being started while the first is still unresolved, which would leave
    /// two contradictory pending records for one copy.
    /// </summary>
    Task<bool> HasAwaitingAsync(string subjectType, Guid subjectId, CancellationToken cancellationToken);

    void Add(PendingAction action);

    Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken);
}
