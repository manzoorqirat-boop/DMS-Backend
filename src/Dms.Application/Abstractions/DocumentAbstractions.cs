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

    /// <param name="search">
    /// Case-insensitive match on document number or title. Null or blank means no filter.
    /// </param>
    Task<PagedResult<ControlledDocument>> ListAsync(
        Guid? siteId,
        Guid? departmentId,
        Guid? documentTypeId,
        bool currentRevisionsOnly,
        string? search,
        PagedRequest paging,
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

    /// <summary>
    /// Effective documents whose next review falls on or before <paramref name="dueBy"/>.
    /// Ordered by due date so the most overdue sits at the top of the report.
    /// </summary>
    /// <summary>
    /// Records whose retention has expired and which have no disposition decision yet — the
    /// disposition worklist. Nothing acts on this automatically; it is a list for a person.
    /// </summary>
    Task<PagedResult<ControlledDocument>> ListDueForDispositionAsync(
        DateOnly asOf,
        Guid? siteId,
        PagedRequest paging,
        CancellationToken cancellationToken);

    Task<PagedResult<ControlledDocument>> ListDueForReviewAsync(
        DateOnly dueBy,
        Guid? siteId,
        Guid? departmentId,
        PagedRequest paging,
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

public interface IReviewPolicyRepository
{
    /// <summary>Policies that could apply to a type at a site. Caller picks the most specific.</summary>
    Task<IReadOnlyList<ReviewPolicy>> FindCandidatesAsync(
        Guid documentTypeId,
        Guid siteId,
        CancellationToken cancellationToken);

    Task<ReviewPolicy?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReviewPolicy>> ListAsync(Guid? documentTypeId, CancellationToken cancellationToken);

    void Add(ReviewPolicy policy);

    Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IDistributionRepository
{
    Task<DocumentDistribution?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<DocumentDistribution>> ListForDocumentAsync(
        Guid documentId,
        CancellationToken cancellationToken);

    /// <summary>Highest copy number issued for a document, or 0. Copy numbers are never reused.</summary>
    Task<int> GetHighestCopyNumberAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>
    /// Copies still in circulation for documents that are no longer current — the retrieval
    /// worklist. Joins to document status rather than requiring a flag to be maintained on the
    /// distribution, so a superseded document can never have stale outstanding-copy state.
    /// </summary>
    Task<PagedResult<(DocumentDistribution Copy, ControlledDocument Document)>> ListPendingRetrievalAsync(
        Guid? siteId,
        PagedRequest paging,
        CancellationToken cancellationToken);

    /// <summary>
    /// Controlled copies issued before a cutoff and still not acknowledged — the chase list.
    /// Uncontrolled copies are excluded: nobody acknowledges an information-only printout.
    /// </summary>
    Task<IReadOnlyList<DocumentDistribution>> ListUnacknowledgedBeforeAsync(
        DateTimeOffset issuedBefore,
        CancellationToken cancellationToken);

    void Add(DocumentDistribution distribution);

    void AddPrintEvent(PrintEvent printEvent);

    Task<PagedResult<PrintEvent>> ListPrintEventsAsync(
        Guid documentId,
        PagedRequest paging,
        CancellationToken cancellationToken);

    Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Renders a watermarked, print-ready copy.
/// <para>
/// An interface because the actual rendering — stamping the watermark across each page and
/// flattening to PDF — needs a document converter that isn't wired up yet. The <b>control</b>
/// around printing is real and enforced regardless: authorisation, print limits, the print
/// record and its audit entry all happen whether or not the bytes come back watermarked.
/// </para>
/// </summary>
public interface IControlledPrintRenderer
{
    Task<PrintRenderResult> RenderAsync(
        byte[] source,
        string watermark,
        string scanCode,
        CancellationToken cancellationToken);
}

/// <param name="IsWatermarked">
/// False when the renderer passed the file through unchanged. Surfaced to the caller rather
/// than hidden, so nobody mistakes an unstamped file for a controlled copy.
/// </param>
public sealed record PrintRenderResult(byte[] Content, string ContentType, bool IsWatermarked);

public interface IRetentionPolicyRepository
{
    Task<IReadOnlyList<RetentionPolicy>> FindCandidatesAsync(
        Guid documentTypeId,
        Guid siteId,
        CancellationToken cancellationToken);

    Task<RetentionPolicy?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<RetentionPolicy>> ListAsync(Guid? documentTypeId, CancellationToken cancellationToken);

    void Add(RetentionPolicy policy);

    Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken);
}
