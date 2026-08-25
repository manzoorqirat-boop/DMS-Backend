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
