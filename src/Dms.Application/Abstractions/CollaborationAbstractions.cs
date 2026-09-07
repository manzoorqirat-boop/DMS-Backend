using Dms.Application.Common;
using Dms.Domain.Entities;

namespace Dms.Application.Abstractions;

/// <summary>Adoptions of globally-issued documents by sites.</summary>
public interface IDocumentAdoptionRepository
{
    Task<DocumentAdoption?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Every adoption of a document, including withdrawn ones. The history is the point — "this
    /// site followed it from March to September" is exactly what an inspector asks about.
    /// </summary>
    Task<IReadOnlyList<DocumentAdoption>> ListForDocumentAsync(
        Guid documentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DocumentAdoption>> ListActiveForSiteAsync(
        Guid siteId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Global documents this site could adopt but has not — the site's adoption worklist.
    /// <para>
    /// Excludes the site's own documents and anything it already has live, which is what makes
    /// it a list of things to act on rather than a catalogue of everything global.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<ControlledDocument>> ListAdoptableForSiteAsync(
        Guid siteId,
        CancellationToken cancellationToken);

    Task<bool> HasActiveAdoptionAsync(Guid documentId, Guid siteId, CancellationToken cancellationToken);

    void Add(DocumentAdoption adoption);

    Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Review comments raised against a document.</summary>
public interface IReviewCommentRepository
{
    Task<ReviewComment?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReviewComment>> ListForDocumentAsync(
        Guid documentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// How many blocking comments are still unanswered. A count rather than a list because the
    /// submission guard needs a number, and it runs on every submit.
    /// </summary>
    Task<int> CountOpenBlockingAsync(Guid documentId, CancellationToken cancellationToken);

    void Add(ReviewComment comment);

    Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken);
}
