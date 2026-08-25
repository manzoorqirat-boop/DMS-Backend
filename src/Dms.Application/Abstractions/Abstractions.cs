using Dms.Application.Common;
using Dms.Domain.Entities;

namespace Dms.Application.Abstractions;

/// <summary>
/// Raw bytes of registered template files. Deliberately an interface with an opaque string
/// key rather than a path: the local-disk implementation is a development convenience, and
/// the deployed system will point this at object storage. Nothing above this interface may
/// assume a filesystem exists.
/// <para>
/// Note for deployment: a container filesystem is ephemeral, so the disk-backed
/// implementation needs a mounted persistent volume, or replacement with an S3/MinIO-backed
/// one, before this is anything other than a dev store.
/// </para>
/// </summary>
public interface ITemplateFileStore
{
    Task SaveAsync(string storageKey, byte[] content, CancellationToken cancellationToken);

    Task<byte[]?> ReadAsync(string storageKey, CancellationToken cancellationToken);

    /// <summary>
    /// Best-effort removal, used to clean up a blob whose database row failed to insert.
    /// Must not throw when the key is already absent.
    /// </summary>
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken);
}

public interface IDocumentTypeRepository
{
    Task<DocumentType?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<DocumentType>> ListAsync(bool includeInactive, CancellationToken cancellationToken);

    void Add(DocumentType documentType);

    Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken);
}

public interface ITemplateRepository
{
    Task<DocumentTemplate?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The single Active template for a type, if one has been activated.</summary>
    Task<DocumentTemplate?> GetActiveAsync(Guid documentTypeId, CancellationToken cancellationToken);

    /// <summary>
    /// Highest <c>TemplateVersion</c> registered for the type, or 0 when none exist. Counts
    /// every status, including Retired and ValidationFailed — a version number is burned once
    /// used, so a failed upload doesn't free its number for reuse and confuse the trail.
    /// </summary>
    Task<int> GetHighestVersionAsync(Guid documentTypeId, CancellationToken cancellationToken);

    Task<PagedResult<DocumentTemplate>> ListAsync(
        Guid? documentTypeId,
        PagedRequest paging,
        CancellationToken cancellationToken);

    void Add(DocumentTemplate template);

    Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Who is performing the current action. Stamped onto <c>DocumentTemplate.CreatedBy</c> and,
/// once Phase 4 lands, onto every audit record.
/// <para>
/// This exists as an abstraction now — before DMS has an auth model — precisely so that
/// wiring real authentication later is a change to one implementation class rather than a
/// change to every service. An attributable actor is a Part 11 §11.10(e) requirement, not a
/// nicety, so services treat a null username as a hard failure rather than substituting
/// "system".
/// </para>
/// </summary>
public interface ICurrentUser
{
    string? UserName { get; }
}
