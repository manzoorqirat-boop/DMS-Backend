using Dms.Application.Common;
using Dms.Domain.Entities;

namespace Dms.Application.Abstractions;

public interface IUserRepository
{
    Task<DmsUser?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<DmsUser?> GetByUserNameAsync(string userName, CancellationToken cancellationToken);

    Task<IReadOnlyList<DmsUser>> ListAsync(bool includeInactive, CancellationToken cancellationToken);

    void Add(DmsUser user);

    Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken);
}

public interface ISignatureRepository
{
    /// <summary>Every step on a document's route, in order.</summary>
    Task<IReadOnlyList<SignatureRequest>> GetRouteAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>Steps awaiting a given user across all documents — their signing queue.</summary>
    Task<IReadOnlyList<SignatureRequest>> GetPendingForUserAsync(
        Guid userId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every step awaiting signature, across every document and user. Used by the reminder
    /// sweep; a per-user query would mean one round trip per user in the system.
    /// </summary>
    Task<IReadOnlyList<SignatureRequest>> ListPendingForAllAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ElectronicSignature>> GetSignaturesAsync(
        Guid documentId,
        CancellationToken cancellationToken);

    void AddRequest(SignatureRequest request);

    void AddSignature(ElectronicSignature signature);

    Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Signing policy. Kept as an injected abstraction rather than constants so the lockout
/// threshold can be tuned per deployment without a code change, while the defaults stay
/// conservative.
/// </summary>
public interface ISigningPolicy
{
    int MaxFailedSigningAttempts { get; }

    TimeSpan LockoutDuration { get; }
}
