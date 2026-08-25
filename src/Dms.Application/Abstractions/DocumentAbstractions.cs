using Dms.Application.Common;
using Dms.Domain.Entities;

namespace Dms.Application.Abstractions;

/// <summary>
/// Working copies of controlled documents. Separate interface from
/// <see cref="ITemplateFileStore"/> rather than one shared store: templates and live documents
/// have different retention, different access rules and will very likely end up in different
/// buckets, and merging them now would be a harder split later.
/// </summary>
public interface IDocumentFileStore
{
    Task SaveAsync(string storageKey, byte[] content, CancellationToken cancellationToken);

    Task<byte[]?> ReadAsync(string storageKey, CancellationToken cancellationToken);

    Task DeleteAsync(string storageKey, CancellationToken cancellationToken);
}

public interface ISiteRepository
{
    Task<Site?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Site>> ListAsync(bool includeInactive, CancellationToken cancellationToken);

    void Add(Site site);

    Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IDepartmentRepository
{
    Task<Department?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Department>> ListAsync(
        Guid? siteId,
        bool includeInactive,
        CancellationToken cancellationToken);

    void Add(Department department);

    Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IControlledDocumentRepository
{
    Task<ControlledDocument?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ControlledDocument>> ListAsync(
        Guid? siteId,
        Guid? departmentId,
        Guid? documentTypeId,
        bool currentRevisionsOnly,
        CancellationToken cancellationToken);

    /// <summary>Every revision of one document, oldest first.</summary>
    Task<IReadOnlyList<ControlledDocument>> ListFamilyAsync(
        Guid familyId,
        CancellationToken cancellationToken);

    /// <summary>The revision currently in force, or the latest if none is yet.</summary>
    Task<ControlledDocument?> GetCurrentRevisionAsync(
        Guid familyId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Any revision of a lineage that is still working its way through draft or review. Used
    /// to stop a second revision being opened while one is already in flight.
    /// </summary>
    Task<ControlledDocument?> GetInFlightRevisionAsync(
        Guid familyId,
        CancellationToken cancellationToken);

    void Add(ControlledDocument document);

    Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reserves the next sequence number for a (site, department, type) combination and
    /// returns it.
    /// <para>
    /// Implemented as one atomic UPSERT rather than load-add-save. Two authors creating an SOP
    /// in the same department at the same instant would otherwise both read the same
    /// <c>LastSequence</c> and both be handed the same number — and a duplicate controlled
    /// document number is not a bug you find in testing, it's one an auditor finds in the
    /// register two years later.
    /// </para>
    /// <para>
    /// Must be called inside <see cref="IUnitOfWork.ExecuteInTransactionAsync{T}"/> together
    /// with the document insert. The row lock the UPSERT takes is held to the end of that
    /// transaction, which is what makes the numbering gap-free: if the insert fails, the
    /// counter rolls back with it and the number is handed to the next caller instead of being
    /// burned.
    /// </para>
    /// </summary>
    /// <param name="periodKey">
    /// Period the counter is scoped to — "2026" when the numbering pattern contains a year
    /// token, empty for a continuous run. Derived from the pattern, so changing the pattern
    /// changes the reset behaviour without any code change.
    /// </param>
    Task<int> AllocateNextSequenceAsync(
        Guid siteId,
        Guid departmentId,
        Guid documentTypeId,
        string periodKey,
        CancellationToken cancellationToken);
}

/// <summary>
/// Runs a unit of work as one database transaction.
/// <para>
/// Needed because draft creation spans a raw-SQL sequence allocation and an EF insert, and
/// those two have to succeed or fail together — a single <c>SaveChanges</c> can't cover both.
/// </para>
/// </summary>
public interface IUnitOfWork
{
    Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);
}
