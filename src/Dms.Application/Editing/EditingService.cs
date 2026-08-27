using Dms.Application.Abstractions;
using Dms.Application.Common;
using Dms.Application.Metadata;
using Dms.Domain.Entities;
using Dms.Domain.Enums;
using Dms.Domain.Services;
using Dms.Domain.Common;

namespace Dms.Application.Editing;

/// <summary>
/// In-browser editing: check-out, the editor launch payload, and the verified save-back.
/// <para>
/// URS Functions #13 forbids the real file ever sitting on a client PC, which rules out
/// download-edit-reupload. The document server holds the working copy server-side and the
/// browser only ever sees a rendered view — this service is what stands between it and the
/// document store.
/// </para>
/// <para>
/// The save path is the important part. Whatever the document server returns is checked by
/// <see cref="DocxProtectionVerifier"/> before it is accepted, because a lock enforced only by
/// the editor is a lock enforced by the client.
/// </para>
/// </summary>
public sealed class EditingService(
    IEditingSessionRepository sessions,
    IControlledDocumentRepository documents,
    ISiteRepository sites,
    IDepartmentRepository departments,
    IDocumentTypeRepository documentTypes,
    IDocumentFileStore documentFiles,
    MetadataFieldService metadataFields,
    IEditorContentFetcher fetcher,
    IEditorTokenService tokens,
    IEditorSettings settings,
    IAccessControl access,
    IAuditTrail audit,
    ICurrentUser currentUser,
    IClock clock)
{
    private const string EntityType = "ControlledDocument";

    /// <summary>
    /// Checks a document out and returns what the browser needs to open it.
    /// <para>
    /// Re-entrant for the holder: reopening your own live session returns the same session
    /// rather than refusing. Losing your browser tab shouldn't cost you your own lock.
    /// </para>
    /// </summary>
    /// <summary>
    /// Opens a document read-only, for anyone who needs to read it rather than change it —
    /// most importantly a reviewer or approver, who cannot meaningfully sign a document they
    /// have not seen.
    /// <para>
    /// Takes <b>no check-out</b> and creates no <see cref="EditingSession"/> row. Reading is
    /// not a mutually exclusive act: several reviewers may read the same document at once, and
    /// none of them should block the author's edit lock or each other. It follows that this
    /// works at any status, not just Draft — reviewers read documents that are precisely
    /// <i>not</i> editable.
    /// </para>
    /// <para>
    /// Because there is no session row, the file token is minted against the DOCUMENT id
    /// rather than a session id. <see cref="IEditorTokenService"/> signs an opaque Guid and
    /// doesn't care what it identifies; the read-only file endpoint interprets it as a
    /// document. That avoids adding a column to EditingSession purely to mark rows that exist
    /// only to be immediately ignored — and, practically, avoids a schema change on a
    /// database created by EnsureCreated, where no migration path exists to apply one.
    /// </para>
    /// </summary>
    public async Task<Result<ViewerLaunchView>> StartViewSessionAsync(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        if (!settings.IsConfigured)
        {
            return Error.Conflict(
                "editor_not_configured",
                "No document server is configured, so documents cannot be viewed in the browser.");
        }

        if (currentUser.UserName is not { } actor || string.IsNullOrWhiteSpace(actor))
        {
            return Error.Validation("actor_unknown", "The acting user could not be determined.");
        }

        var document = await documents.GetAsync(documentId, cancellationToken);
        if (document is null)
        {
            return Error.NotFound("document_not_found", $"No document with id {documentId}.");
        }

        // DocumentView, not DocumentEdit: a reviewer who may sign a document but not author it
        // still has to be able to read it. Requiring DocumentEdit here would have made the
        // review step impossible for exactly the people it exists for.
        var permitted = await access.HasPermissionAsync(
            Permission.DocumentView, document.SiteId, document.DepartmentId, cancellationToken);

        if (!permitted)
        {
            return Error.Validation(
                "permission_denied",
                $"{Permission.DocumentView} is required for this document's site and department.");
        }

        if (string.IsNullOrWhiteSpace(document.WorkingCopyKey))
        {
            return Error.NotFound(
                "document_file_missing",
                $"{document.DocumentNumber} has no stored file to display.");
        }

        var token = tokens.Issue(document.Id, clock.UtcNow.Add(settings.SessionLifetime));
        var root = settings.CallbackBaseUrl.TrimEnd('/');

        // The cache key must change whenever the content does, or the document server serves a
        // reader the previous revision from its own cache. ApprovedContentHash is ideal once a
        // document is approved — it is exactly the hash the signatures are bound to. Before
        // that it is null, so a draft falls back to a per-minute key: still cached usefully
        // within a reading session, but never stale for more than a minute while an author is
        // actively editing.
        var cacheKey = document.ApprovedContentHash is { Length: > 0 } hash
            ? $"{document.Id:N}-{hash[..Math.Min(16, hash.Length)]}"
            : $"{document.Id:N}-r{document.Revision}-{clock.UtcNow:yyyyMMddHHmm}";

        return new ViewerLaunchView(
            document.Id,
            document.DocumentNumber,
            document.Title,
            $"{document.Revision:00}",
            cacheKey,
            settings.DocumentServerUrl,
            $"{root}/api/public/editor/{token}/view-file",
            actor);
    }

    /// <summary>
    /// Serves a document's file to the document server for a read-only view.
    /// <para>
    /// Distinct from <see cref="GetFileForEditorAsync"/> because the token here identifies a
    /// document, not a session — see the remarks on <see cref="StartViewSessionAsync"/>. There
    /// is deliberately no matching callback: nothing this endpoint serves can be written back.
    /// </para>
    /// </summary>
    public async Task<Result<(byte[] Content, string FileName)>> GetFileForViewerAsync(
        string token,
        CancellationToken cancellationToken)
    {
        if (tokens.Validate(token) is not { } documentId)
        {
            return Error.Validation("editor_token_invalid", "The viewing link is invalid or has expired.");
        }

        var document = await documents.GetAsync(documentId, cancellationToken);
        if (document is null)
        {
            return Error.NotFound("document_not_found", "The document no longer exists.");
        }

        var content = await documentFiles.ReadAsync(document.WorkingCopyKey, cancellationToken);

        return content is null
            ? Error.NotFound("document_file_missing", "The stored file is missing.")
            : Result<(byte[] Content, string FileName)>.Success(
                (content, $"{document.DocumentNumber}.docx"));
    }

    public async Task<Result<EditorLaunchView>> StartSessionAsync(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        if (!settings.IsConfigured)
        {
            return Error.Conflict(
                "editor_not_configured",
                "No document server is configured, so documents cannot be edited in the browser.");
        }

        if (currentUser.UserName is not { } actor || string.IsNullOrWhiteSpace(actor))
        {
            return Error.Validation("actor_unknown", "The acting user could not be determined.");
        }

        var document = await documents.GetAsync(documentId, cancellationToken);
        if (document is null)
        {
            return Error.NotFound("document_not_found", $"No document with id {documentId}.");
        }

        var permitted = await access.HasPermissionAsync(
            Permission.DocumentEdit, document.SiteId, document.DepartmentId, cancellationToken);

        if (!permitted)
        {
            return Error.Validation(
                "permission_denied",
                $"{Permission.DocumentEdit} is required for this document's site and department.");
        }

        if (!document.IsEditable)
        {
            return Error.Conflict(
                "document_not_editable",
                $"{document.DocumentNumber} is {document.Status}. Only a Draft can be edited; "
                + "a document in review is frozen against the hash its signatures are applied to.");
        }

        var existing = await sessions.GetActiveForDocumentAsync(documentId, cancellationToken);

        if (existing is not null)
        {
            if (string.Equals(existing.UserName, actor, StringComparison.OrdinalIgnoreCase))
            {
                existing.Touch(clock.UtcNow, settings.SessionLifetime);
                await sessions.SaveChangesAsync(cancellationToken);
                return Launch(document, existing);
            }

            if (!existing.HasExpired(clock.UtcNow))
            {
                return Error.Conflict(
                    "document_checked_out",
                    $"{document.DocumentNumber} is checked out by {existing.UserName} until "
                    + $"{existing.ExpiresAt:yyyy-MM-dd HH:mm} UTC.");
            }

            // Lapsed lock — taken over rather than requiring an administrator. Recorded as a
            // distinct event so a pattern of abandoned sessions is visible.
            existing.Close(EditingSessionStatus.Abandoned, actor, "Lock expired and was taken over.");
            audit.Record(
                AuditAction.EditingSessionForceClosed, EntityType, document.Id, document.DocumentNumber,
                $"Expired session held by {existing.UserName} taken over by {actor}.");
        }

        // A fresh key every session: document servers cache against it, and reusing one would
        // serve a stale copy of a document that has since changed.
        var session = new EditingSession(
            document.Id,
            actor,
            Uuid7.NewGuid().ToString("N"),
            clock.UtcNow.Add(settings.SessionLifetime));

        sessions.Add(session);
        audit.Record(
            AuditAction.DocumentCheckedOut, EntityType, document.Id,
            $"{document.DocumentNumber} Rev {document.Revision:00}",
            $"Checked out by {actor} until {session.ExpiresAt:yyyy-MM-dd HH:mm} UTC.");

        var outcome = await sessions.SaveChangesAsync(cancellationToken);
        if (!outcome.Saved)
        {
            return outcome.ViolatedIndexContains("one_active")
                ? Error.Conflict(
                    "document_checked_out",
                    "Someone else checked the document out a moment ago. Reload and retry.")
                : Error.Conflict("session_save_conflict", "The document could not be checked out.");
        }

        return Launch(document, session);
    }

    /// <summary>
    /// The working copy, for the document server to open. Authorised by the session token
    /// alone — the document server has no user session, so this is the only credential it can
    /// present.
    /// </summary>
    public async Task<Result<(byte[] Content, string FileName)>> GetFileForEditorAsync(
        string token,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveTokenAsync(token, cancellationToken);
        if (!resolved.IsSuccess)
        {
            return resolved.Error!;
        }

        var (_, document) = resolved.Value;

        var content = await documentFiles.ReadAsync(document.WorkingCopyKey, cancellationToken);
        return content is null
            ? Error.NotFound("document_file_missing", "The working copy is missing.")
            : Result<(byte[] Content, string FileName)>.Success(
                (content, $"{document.DocumentNumber}.docx"));
    }

    /// <summary>
    /// Handles the document server's save callback.
    /// <para>
    /// On a save-bearing status the returned file is fetched and revalidated before it
    /// replaces anything. A file that fails verification is <b>quarantined, not discarded and
    /// not applied</b> — discarding would destroy the author's work, applying it would accept a
    /// document whose protected regions were altered. The callback is answered with a non-zero
    /// error so the document server keeps the file and the author is told.
    /// </para>
    /// </summary>
    public async Task<EditorCallbackResult> HandleCallbackAsync(
        string token,
        EditorCallback callback,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveTokenAsync(token, cancellationToken);
        if (!resolved.IsSuccess)
        {
            return EditorCallbackResult.Rejected(resolved.Error!.Message);
        }

        var (session, document) = resolved.Value;

        switch (callback.Status)
        {
            case 1:
                // Still editing. Used as a heartbeat to hold the lock open.
                session.Touch(clock.UtcNow, settings.SessionLifetime);
                await sessions.SaveChangesAsync(cancellationToken);
                return EditorCallbackResult.Accepted;

            case 4:
                session.Close(EditingSessionStatus.Abandoned, session.UserName, "Closed with no changes.");
                audit.Record(
                    AuditAction.DocumentCheckedIn, EntityType, document.Id, document.DocumentNumber,
                    $"Checked in by {session.UserName} with no changes.");
                await sessions.SaveChangesAsync(cancellationToken);
                return EditorCallbackResult.Accepted;

            case 2:
            case 6:
                return await ApplySaveAsync(session, document, callback, forceSave: callback.Status == 6, cancellationToken);

            case 3:
            case 7:
                // The document server itself failed to produce a file. The lock stays — the
                // author's work is still on that server and closing the session here would
                // strand it.
                audit.Record(
                    AuditAction.EditingSaveRejected, EntityType, document.Id, document.DocumentNumber,
                    $"Document server reported a save error (status {callback.Status}). Session left open.");
                await sessions.SaveChangesAsync(cancellationToken);
                return EditorCallbackResult.Rejected("Document server reported a save error.");

            default:
                return EditorCallbackResult.Accepted;
        }
    }

    private async Task<EditorCallbackResult> ApplySaveAsync(
        EditingSession session,
        ControlledDocument document,
        EditorCallback callback,
        bool forceSave,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(callback.Url))
        {
            return EditorCallbackResult.Rejected("Save callback carried no document URL.");
        }

        var content = await fetcher.FetchAsync(callback.Url, cancellationToken);
        if (content is null || content.Length == 0)
        {
            audit.Record(
                AuditAction.EditingSaveRejected, EntityType, document.Id, document.DocumentNumber,
                "Saved document could not be retrieved from the document server.");
            await sessions.SaveChangesAsync(cancellationToken);
            return EditorCallbackResult.Rejected("Saved document could not be retrieved.");
        }

        var expected = await BuildExpectedMetadataAsync(document, cancellationToken);
        if (expected is null)
        {
            return EditorCallbackResult.Rejected("The document's master data could not be resolved.");
        }

        var verification = DocxProtectionVerifier.Verify(content, expected);

        if (!verification.IsValid)
        {
            // Quarantined under its own key so it can be inspected, and deliberately not
            // written over the working copy.
            var quarantineKey = $"quarantine/{document.Id:N}/{Uuid7.NewGuid():N}.docx";
            await documentFiles.SaveAsync(quarantineKey, content, cancellationToken);

            audit.Record(
                AuditAction.EditingSaveRejected, EntityType, document.Id, document.DocumentNumber,
                $"Save rejected — protected content was altered. {string.Join(" | ", verification.Findings)} "
                + $"Rejected file retained for inspection at {quarantineKey}.");

            audit.Record(
                AuditAction.DocumentIntegrityCheckFailed, EntityType, document.Id, document.DocumentNumber,
                string.Join(" | ", verification.Findings));

            await sessions.SaveChangesAsync(cancellationToken);

            return EditorCallbackResult.Rejected(
                "The saved document failed integrity checks: system-populated fields or document "
                + "protection were altered. The file has been retained for review and not applied.");
        }

        await documentFiles.SaveAsync(document.WorkingCopyKey, content, cancellationToken);
        session.RecordSave(clock.UtcNow);

        audit.Record(
            AuditAction.EditingSaveAccepted, EntityType, document.Id, document.DocumentNumber,
            $"Save {session.SaveCount} accepted from {session.UserName}"
            + (forceSave ? " (force save; session left open)." : "."));

        if (!forceSave)
        {
            // Status 2 means the editor closed. Check the document back in.
            session.Close(EditingSessionStatus.CheckedIn, session.UserName);
            audit.Record(
                AuditAction.DocumentCheckedIn, EntityType, document.Id, document.DocumentNumber,
                $"Checked in by {session.UserName} after {session.SaveCount} save(s).");
        }

        await sessions.SaveChangesAsync(cancellationToken);
        return EditorCallbackResult.Accepted;
    }

    /// <summary>
    /// Releases a check-out without waiting for it to lapse. The holder may always release
    /// their own; breaking someone else's needs <see cref="Permission.DocumentEdit"/> and is
    /// recorded as a force-close rather than a normal check-in.
    /// </summary>
    public async Task<Result<EditingSessionView>> ReleaseAsync(
        Guid documentId,
        string? note,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserName is not { } actor || string.IsNullOrWhiteSpace(actor))
        {
            return Error.Validation("actor_unknown", "The acting user could not be determined.");
        }

        var document = await documents.GetAsync(documentId, cancellationToken);
        if (document is null)
        {
            return Error.NotFound("document_not_found", $"No document with id {documentId}.");
        }

        var session = await sessions.GetActiveForDocumentAsync(documentId, cancellationToken);
        if (session is null)
        {
            return Error.Conflict("not_checked_out", $"{document.DocumentNumber} is not checked out.");
        }

        var isHolder = string.Equals(session.UserName, actor, StringComparison.OrdinalIgnoreCase);

        if (!isHolder)
        {
            var permitted = await access.HasPermissionAsync(
                Permission.DocumentEdit, document.SiteId, document.DepartmentId, cancellationToken);

            if (!permitted)
            {
                return Error.Validation(
                    "permission_denied",
                    $"{document.DocumentNumber} is checked out by {session.UserName}, and "
                    + $"{Permission.DocumentEdit} is required to release someone else's lock.");
            }
        }

        session.Close(
            isHolder ? EditingSessionStatus.CheckedIn : EditingSessionStatus.ForceClosed,
            actor,
            note);

        audit.Record(
            isHolder ? AuditAction.DocumentCheckedIn : AuditAction.EditingSessionForceClosed,
            EntityType, document.Id, document.DocumentNumber,
            isHolder
                ? $"Checked in by {actor}."
                : $"Lock held by {session.UserName} force-released by {actor}. {note ?? ""}".TrimEnd());

        var outcome = await sessions.SaveChangesAsync(cancellationToken);
        return outcome.Saved
            ? EditingSessionView.From(session)
            : Error.Conflict("session_save_conflict", "The session could not be closed.");
    }

    public async Task<Result<IReadOnlyList<EditingSessionView>>> ListSessionsAsync(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var document = await documents.GetAsync(documentId, cancellationToken);
        if (document is null)
        {
            return Error.NotFound("document_not_found", $"No document with id {documentId}.");
        }

        var found = await sessions.ListForDocumentAsync(documentId, cancellationToken);
        return Result<IReadOnlyList<EditingSessionView>>.Success(
            found.Select(EditingSessionView.From).ToList());
    }

    private EditorLaunchView Launch(ControlledDocument document, EditingSession session)
    {
        var token = tokens.Issue(session.Id, session.ExpiresAt);
        var root = settings.CallbackBaseUrl.TrimEnd('/');

        return new EditorLaunchView(
            session.Id,
            document.Id,
            document.DocumentNumber,
            document.Title,
            session.SessionKey,
            settings.DocumentServerUrl,
            $"{root}/api/public/editor/{token}/file",
            $"{root}/api/public/editor/{token}/callback",
            session.UserName,
            session.ExpiresAt);
    }

    private async Task<Result<(EditingSession Session, ControlledDocument Document)>> ResolveTokenAsync(
        string token,
        CancellationToken cancellationToken)
    {
        if (tokens.Validate(token) is not { } sessionId)
        {
            return Error.Validation("editor_token_invalid", "The editing token is invalid or has expired.");
        }

        var session = await sessions.GetAsync(sessionId, cancellationToken);
        if (session is null || !session.IsActive)
        {
            return Error.Conflict("editing_session_closed", "The editing session is no longer open.");
        }

        var document = await documents.GetAsync(session.DocumentId, cancellationToken);
        if (document is null)
        {
            return Error.NotFound("document_not_found", "The document being edited no longer exists.");
        }

        return Result<(EditingSession Session, ControlledDocument Document)>.Success((session, document));
    }

    /// <summary>
    /// The metadata the server wrote, rebuilt through the same resolver used at creation so a
    /// formatting difference can't masquerade as tampering.
    /// </summary>
    private async Task<Dictionary<string, string>?> BuildExpectedMetadataAsync(
        ControlledDocument document,
        CancellationToken cancellationToken)
    {
        var site = await sites.GetAsync(document.SiteId, cancellationToken);
        var department = await departments.GetAsync(document.DepartmentId, cancellationToken);
        var documentType = await documentTypes.GetAsync(document.DocumentTypeId, cancellationToken);

        if (site is null || department is null || documentType is null)
        {
            return null;
        }

        var definitions = await metadataFields.ResolveForTypeAsync(document.DocumentTypeId, cancellationToken);

        return MetadataResolver.Resolve(definitions, new MetadataContext(
            document.DocumentNumber,
            document.Title,
            document.Revision,
            document.EffectiveDate,
            site.Code,
            site.Name,
            department.Code,
            department.Name,
            documentType.Code,
            documentType.Name,
            document.Author,
            document.Author,
            document.CreatedAt,
            document.Status));
    }
}
