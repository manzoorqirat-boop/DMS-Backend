using Dms.Application.Common;
using Dms.Domain.Entities;

namespace Dms.Application.Abstractions;

public interface IEditingSessionRepository
{
    Task<EditingSession?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The open session for a document, if one exists. This is the check-out lock.</summary>
    Task<EditingSession?> GetActiveForDocumentAsync(Guid documentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<EditingSession>> ListForDocumentAsync(Guid documentId, CancellationToken cancellationToken);

    void Add(EditingSession session);

    Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Fetches a saved document back from the document server.
/// <para>
/// The server pushes a callback containing a URL rather than the bytes, so DMS has to go and
/// collect them. Behind an interface because that URL points at infrastructure DMS doesn't
/// own, and because a test must be able to exercise the save path without a document server
/// running.
/// </para>
/// </summary>
public interface IEditorContentFetcher
{
    Task<byte[]?> FetchAsync(string url, CancellationToken cancellationToken);
}

/// <summary>
/// Mints and validates the short-lived tokens in the URLs handed to the document server.
/// <para>
/// The document server is a separate process calling back over HTTP with no user session, so
/// those routes can't be protected by the normal login. A signed, expiring, single-session
/// token is what stands in for it — without one, the file and callback endpoints would be
/// open to anyone who guessed a document id.
/// </para>
/// </summary>
public interface IEditorTokenService
{
    string Issue(Guid sessionId, DateTimeOffset expiresAt);

    /// <summary>Returns the session id when the token is valid, signed and unexpired; null otherwise.</summary>
    Guid? Validate(string token);
}

/// <summary>Deployment settings for the document server integration.</summary>
public interface IEditorSettings
{
    /// <summary>Base URL of the document server, used to build the editor script src.</summary>
    string DocumentServerUrl { get; }

    /// <summary>
    /// Public base URL of this API <b>as the document server sees it</b>. Often different from
    /// what a browser uses — the server is typically inside the same network and cannot resolve
    /// an external hostname.
    /// </summary>
    string CallbackBaseUrl { get; }

    /// <summary>How long a check-out lasts before it can be taken over.</summary>
    TimeSpan SessionLifetime { get; }

    /// <summary>True when a document server is actually configured.</summary>
    bool IsConfigured { get; }
}

/// <summary>
/// Converts an Office document to PDF.
/// <para>
/// Separate from <see cref="IControlledPrintRenderer"/> because the two answer different
/// questions: the renderer decides what a controlled copy should look like (watermark, scan
/// code), while this only changes format. Keeping them apart means the approved-PDF path
/// doesn't inherit watermarking it must not have — an approved record is not a controlled
/// copy, and stamping it "CONTROLLED COPY 3 OF 5" would be actively wrong.
/// </para>
/// </summary>
public interface IDocumentConverter
{
    /// <summary>
    /// Returns PDF bytes. Throws rather than returning the source unconverted: a caller that
    /// asked for a PDF and silently received a .docx would store it under a .pdf key and only
    /// discover the problem when someone tried to open it.
    /// </summary>
    Task<byte[]> ToPdfAsync(byte[] docx, CancellationToken cancellationToken);

    /// <summary>False when no document server is configured, so callers can degrade honestly.</summary>
    bool IsAvailable { get; }
}
