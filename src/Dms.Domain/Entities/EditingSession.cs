using Dms.Domain.Common;
using Dms.Domain.Enums;

namespace Dms.Domain.Entities;

/// <summary>
/// One check-out of a document for editing in the browser-based document server.
/// <para>
/// This <b>is</b> the check-in/check-out mechanism URS Functions #28 asks for. A document with
/// an active session is checked out; closing the session checks it back in. Modelling it as a
/// session rather than a boolean flag means the lock carries who holds it, since when, and
/// when it expires — a bare "locked" flag strands documents the moment someone closes their
/// laptop mid-edit.
/// </para>
/// </summary>
public class EditingSession : Entity
{
    private EditingSession() { }

    public EditingSession(Guid documentId, string userName, string sessionKey, DateTimeOffset expiresAt)
    {
        DocumentId = documentId;
        UserName = RequireNonEmpty(userName, nameof(userName));
        SessionKey = RequireNonEmpty(sessionKey, nameof(sessionKey));
        ExpiresAt = expiresAt;
        Status = EditingSessionStatus.Active;
        StartedAt = DateTimeOffset.UtcNow;
        LastActivityAt = StartedAt;
    }

    public Guid DocumentId { get; private set; }

    /// <summary>Who holds the check-out.</summary>
    public string UserName { get; private set; } = "";

    /// <summary>
    /// Opaque key handed to the document server to identify this editing instance.
    /// <para>
    /// Freshly generated per session, never reused. Document servers cache aggressively
    /// against this key — reusing it across sessions serves the author a stale copy of a
    /// document that has since changed, which on a controlled document means editing a version
    /// that no longer exists.
    /// </para>
    /// </summary>
    public string SessionKey { get; private set; } = "";

    public EditingSessionStatus Status { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>
    /// When the lock lapses. A session that outlives this can be taken over without an
    /// administrator having to force it, which is the common case: someone opened a document
    /// and went home.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset LastActivityAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public string? ClosedBy { get; private set; }

    public string? ClosureNote { get; private set; }

    /// <summary>How many accepted saves this session produced. Force-saves increment it too.</summary>
    public int SaveCount { get; private set; }

    public bool IsActive => Status == EditingSessionStatus.Active;

    public bool HasExpired(DateTimeOffset now) => IsActive && ExpiresAt <= now;

    /// <summary>Extends the lock while the author is demonstrably still working.</summary>
    public void Touch(DateTimeOffset now, TimeSpan extendBy)
    {
        if (!IsActive)
        {
            throw new InvalidOperationException($"Session for {DocumentId} is {Status} and cannot be extended.");
        }

        LastActivityAt = now;
        ExpiresAt = now.Add(extendBy);
    }

    public void RecordSave(DateTimeOffset now)
    {
        if (!IsActive)
        {
            throw new InvalidOperationException($"Session for {DocumentId} is {Status}; saves are not accepted.");
        }

        SaveCount++;
        LastActivityAt = now;
    }

    public void Close(EditingSessionStatus outcome, string closedBy, string? note = null)
    {
        if (!IsActive)
        {
            throw new InvalidOperationException($"Session for {DocumentId} is already {Status}.");
        }

        if (outcome == EditingSessionStatus.Active)
        {
            throw new ArgumentException("Closing a session needs a terminal outcome.", nameof(outcome));
        }

        Status = outcome;
        ClosedBy = RequireNonEmpty(closedBy, nameof(closedBy));
        ClosureNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        ClosedAt = DateTimeOffset.UtcNow;
    }

    private static string RequireNonEmpty(string value, string paramName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{paramName} is required.", paramName)
            : value.Trim();
}
