using Dms.Domain.Entities;
using Dms.Domain.Enums;

namespace Dms.Application.Editing;

/// <summary>
/// Everything the browser needs to mount the editor. Deliberately not the document server's
/// own config object — DMS returns its own shape and the frontend assembles the vendor payload,
/// so swapping document servers doesn't change this contract.
/// </summary>
public sealed record EditorLaunchView(
    Guid SessionId,
    Guid DocumentId,
    string DocumentNumber,
    string Title,
    string SessionKey,
    string DocumentServerUrl,
    string FileUrl,
    string CallbackUrl,
    string EditorUserName,
    DateTimeOffset ExpiresAt);

public sealed record EditingSessionView(
    Guid Id,
    Guid DocumentId,
    string UserName,
    EditingSessionStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ClosedAt,
    string? ClosedBy,
    int SaveCount)
{
    public static EditingSessionView From(EditingSession session) => new(
        session.Id,
        session.DocumentId,
        session.UserName,
        session.Status,
        session.StartedAt,
        session.ExpiresAt,
        session.ClosedAt,
        session.ClosedBy,
        session.SaveCount);
}

/// <summary>
/// The document server's save callback, normalised. Status codes follow the OnlyOffice
/// convention: 1 editing, 2 ready to save, 3 save error, 4 closed unchanged, 6 force save,
/// 7 force save error.
/// </summary>
public sealed record EditorCallback(int Status, string? Url, string[]? Users);

/// <summary>
/// What the document server expects back. Zero means accepted; anything else makes it hold
/// the document and retry, which is what we want when a save is rejected.
/// </summary>
public sealed record EditorCallbackResult(int Error, string? Message)
{
    public static readonly EditorCallbackResult Accepted = new(0, null);

    public static EditorCallbackResult Rejected(string message) => new(1, message);
}
