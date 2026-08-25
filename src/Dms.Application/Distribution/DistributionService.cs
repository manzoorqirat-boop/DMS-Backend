using Dms.Application.Abstractions;
using Dms.Application.Common;
using Dms.Domain.Entities;
using Dms.Domain.Enums;
using Dms.Domain.Services;

namespace Dms.Application.Distribution;

/// <summary>
/// Issue, acknowledgement, controlled printing and retrieval of physical copies.
/// <para>
/// The register of who holds what is the reason this exists. When a document is superseded or
/// withdrawn, every controlled copy in circulation has to be physically collected — and that
/// is only possible if the system recorded where each one went.
/// </para>
/// </summary>
public sealed class DistributionService(
    IDistributionRepository distributions,
    IControlledDocumentRepository documents,
    IDepartmentRepository departments,
    IDocumentFileStore documentFiles,
    IControlledPrintRenderer renderer,
    IAccessControl access,
    IAuditTrail audit,
    ICurrentUser currentUser)
{
    private const string EntityType = "DocumentDistribution";

    /// <summary>
    /// Issues a numbered copy of an effective document.
    /// <para>
    /// Restricted to Effective documents. Distributing a draft or an approved-but-not-yet-in-
    /// force version puts a procedure into someone's hands before it is the procedure, which
    /// is the distribution failure that actually causes harm on a shop floor.
    /// </para>
    /// </summary>
    public async Task<Result<DistributionView>> IssueAsync(
        Guid documentId,
        IssueCopyRequest request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserName is not { } actor || string.IsNullOrWhiteSpace(actor))
        {
            return Error.Validation(
                "actor_unknown",
                "The acting user could not be determined. Copy issue must be attributable.");
        }

        var document = await documents.GetAsync(documentId, cancellationToken);
        if (document is null)
        {
            return Error.NotFound("document_not_found", $"No document with id {documentId}.");
        }

        var permitted = await access.HasPermissionAsync(
            Permission.DocumentIssue, document.SiteId, document.DepartmentId, cancellationToken);

        if (!permitted)
        {
            return Error.Validation(
                "permission_denied",
                $"{Permission.DocumentIssue} is required for this document's site and department.");
        }

        if (document.Status != DocumentStatus.Effective)
        {
            return Error.Conflict(
                "document_not_distributable",
                $"{document.DocumentNumber} is {document.Status}; only an "
                + $"{DocumentStatus.Effective} document can be distributed.");
        }

        if (request.IssuedToDepartmentId is { } departmentId)
        {
            var department = await departments.GetAsync(departmentId, cancellationToken);
            if (department is null)
            {
                return Error.NotFound("department_not_found", $"No department with id {departmentId}.");
            }
        }

        // A controlled copy with no print limit can be reprinted indefinitely, which makes the
        // copy number meaningless — the one thing that distinguishes it from an uncontrolled
        // printout. Uncontrolled copies may legitimately be unlimited.
        if (request.CopyType != CopyType.Uncontrolled && request.PrintLimit is null)
        {
            return Error.Validation(
                "print_limit_required",
                $"A {request.CopyType} copy needs a print limit; only an "
                + $"{CopyType.Uncontrolled} copy may be unlimited.");
        }

        var highest = await distributions.GetHighestCopyNumberAsync(documentId, cancellationToken);

        DocumentDistribution copy;
        try
        {
            copy = new DocumentDistribution(
                documentId,
                highest + 1,
                request.CopyType,
                request.IssuedToDepartmentId,
                request.IssuedToName,
                actor,
                request.PrintLimit);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return Error.Validation("copy_invalid", ex.Message);
        }

        distributions.Add(copy);
        audit.Record(
            AuditAction.CopyIssued, EntityType, copy.Id,
            $"{document.DocumentNumber} Rev {document.Revision:00} copy {copy.CopyNumber}",
            $"{copy.CopyType} copy issued to {copy.IssuedToName}"
            + (copy.PrintLimit is { } limit ? $", print limit {limit}." : ", unlimited prints."));

        var outcome = await distributions.SaveChangesAsync(cancellationToken);
        if (!outcome.Saved)
        {
            return outcome.ViolatedIndexContains("copy_number")
                ? Error.Conflict(
                    "copy_number_conflict",
                    "Another copy was issued concurrently and took that number. Retry.")
                : Error.Conflict("copy_save_conflict", "The copy could not be issued.");
        }

        return DistributionView.From(copy, ScanCodeFor(document, copy));
    }

    public async Task<Result<DistributionView>> AcknowledgeAsync(
        Guid distributionId,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(distributionId, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        var (copy, document) = loaded.Value;

        // Acknowledgement is the recipient's own act, so it is attributable to whoever is
        // logged in rather than to the issuer recording it on their behalf.
        if (currentUser.UserName is not { } actor || string.IsNullOrWhiteSpace(actor))
        {
            return Error.Validation("actor_unknown", "The acting user could not be determined.");
        }

        try
        {
            copy.Acknowledge(actor);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Error.Conflict("copy_not_acknowledgeable", ex.Message);
        }

        audit.Record(
            AuditAction.CopyAcknowledged, EntityType, copy.Id,
            $"{document.DocumentNumber} copy {copy.CopyNumber}",
            $"Receipt confirmed by {actor}.");

        var outcome = await distributions.SaveChangesAsync(cancellationToken);
        return outcome.Saved
            ? DistributionView.From(copy, ScanCodeFor(document, copy))
            : Error.Conflict("copy_save_conflict", "The acknowledgement could not be recorded.");
    }

    public async Task<Result<DistributionView>> RetrieveAsync(
        Guid distributionId,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(distributionId, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        var (copy, document) = loaded.Value;

        var gate = await RequireIssuePermissionAsync(document, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        try
        {
            copy.Retrieve(currentUser.UserName!);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Error.Conflict("copy_not_retrievable", ex.Message);
        }

        audit.Record(
            AuditAction.CopyRetrieved, EntityType, copy.Id,
            $"{document.DocumentNumber} copy {copy.CopyNumber}",
            $"Collected from {copy.IssuedToName}.");

        var outcome = await distributions.SaveChangesAsync(cancellationToken);
        return outcome.Saved
            ? DistributionView.From(copy, ScanCodeFor(document, copy))
            : Error.Conflict("copy_save_conflict", "The retrieval could not be recorded.");
    }

    /// <summary>
    /// Closes out a copy that can't be collected — destroyed on site, or lost. Both require a
    /// note; a copy that simply disappears from the register without explanation is worse than
    /// one recorded as missing.
    /// </summary>
    public async Task<Result<DistributionView>> CloseOutAsync(
        Guid distributionId,
        CloseOutRequest request,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(distributionId, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        var (copy, document) = loaded.Value;

        var gate = await RequireIssuePermissionAsync(document, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        try
        {
            copy.CloseOut(request.Outcome, request.Note, currentUser.UserName!);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Error.Conflict("copy_not_closeable", ex.Message);
        }

        audit.Record(
            AuditAction.CopyClosedOut, EntityType, copy.Id,
            $"{document.DocumentNumber} copy {copy.CopyNumber}",
            $"Recorded as {request.Outcome}. {request.Note.Trim()}");

        var outcome = await distributions.SaveChangesAsync(cancellationToken);
        return outcome.Saved
            ? DistributionView.From(copy, ScanCodeFor(document, copy))
            : Error.Conflict("copy_save_conflict", "The close-out could not be recorded.");
    }

    /// <summary>
    /// Produces a print of a copy, enforcing its limit and recording the event.
    /// <para>
    /// A refused print is audited too. Someone repeatedly hitting a print limit is a signal —
    /// either the limit is wrong or copies are going somewhere they shouldn't — and it is only
    /// visible if the refusals are recorded rather than silently returned as an error.
    /// </para>
    /// </summary>
    public async Task<Result<PrintResult>> PrintAsync(
        Guid distributionId,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(distributionId, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        var (copy, document) = loaded.Value;

        if (currentUser.UserName is not { } actor || string.IsNullOrWhiteSpace(actor))
        {
            return Error.Validation("actor_unknown", "The acting user could not be determined.");
        }

        if (!copy.CanPrint)
        {
            audit.Record(
                AuditAction.CopyPrintRefused, EntityType, copy.Id,
                $"{document.DocumentNumber} copy {copy.CopyNumber}",
                copy.IsOutstanding
                    ? $"Print limit of {copy.PrintLimit} already reached."
                    : $"Copy is {copy.Status} and cannot be reprinted.");

            await distributions.SaveChangesAsync(cancellationToken);

            return Error.Conflict(
                "print_not_permitted",
                copy.IsOutstanding
                    ? $"Copy {copy.CopyNumber} has reached its print limit of {copy.PrintLimit}."
                    : $"Copy {copy.CopyNumber} is {copy.Status} and cannot be reprinted.");
        }

        // Read after the permission and limit checks, so a refused print never touches the
        // blob store.
        var documentContent = await documentFiles.ReadAsync(document.WorkingCopyKey, cancellationToken);
        if (documentContent is null)
        {
            return Error.NotFound(
                "document_file_missing",
                $"The stored file for {document.DocumentNumber} is missing.");
        }

        int sequence;
        try
        {
            sequence = copy.RecordPrint();
        }
        catch (InvalidOperationException ex)
        {
            return Error.Conflict("print_not_permitted", ex.Message);
        }

        var watermark = ControlledCopyWatermark.Compose(
            copy.CopyType,
            document.DocumentNumber,
            document.Revision,
            copy.CopyNumber,
            sequence,
            copy.IssuedToName,
            DateTimeOffset.UtcNow);

        var scanCode = ScanCodeFor(document, copy);

        var rendered = await renderer.RenderAsync(documentContent, watermark, scanCode, cancellationToken);

        distributions.AddPrintEvent(new PrintEvent(copy.Id, document.Id, sequence, actor, watermark));

        audit.Record(
            AuditAction.CopyPrinted, EntityType, copy.Id,
            $"{document.DocumentNumber} copy {copy.CopyNumber}",
            $"Print {sequence} of {copy.PrintLimit?.ToString() ?? "unlimited"} by {actor}."
            + (rendered.IsWatermarked ? "" : " NOT WATERMARKED — renderer unavailable."));

        var outcome = await distributions.SaveChangesAsync(cancellationToken);
        if (!outcome.Saved)
        {
            return Error.Conflict("copy_save_conflict", "The print could not be recorded.");
        }

        return Result<PrintResult>.Success(new PrintResult(
            rendered.Content,
            rendered.ContentType,
            rendered.IsWatermarked,
            watermark,
            scanCode,
            sequence,
            copy.PrintCount,
            copy.PrintLimit));
    }

    public async Task<Result<IReadOnlyList<DistributionView>>> ListForDocumentAsync(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var document = await documents.GetAsync(documentId, cancellationToken);
        if (document is null)
        {
            return Error.NotFound("document_not_found", $"No document with id {documentId}.");
        }

        var copies = await distributions.ListForDocumentAsync(documentId, cancellationToken);

        return Result<IReadOnlyList<DistributionView>>.Success(
            copies.Select(c => DistributionView.From(c, ScanCodeFor(document, c))).ToList());
    }

    /// <summary>
    /// The retrieval worklist: copies still in circulation for documents that are no longer
    /// current. This is what someone works through after a supersession.
    /// </summary>
    public async Task<PagedResult<PendingRetrievalView>> ListPendingRetrievalAsync(
        Guid? siteId,
        PagedRequest paging,
        CancellationToken cancellationToken)
    {
        var pending = await distributions.ListPendingRetrievalAsync(siteId, paging, cancellationToken);

        return pending
            .Map(x => new PendingRetrievalView(
                x.Copy.Id,
                x.Document.Id,
                x.Document.DocumentNumber,
                x.Document.Revision,
                x.Document.Title,
                x.Document.Status,
                x.Copy.CopyNumber,
                x.Copy.CopyType,
                x.Copy.IssuedToName,
                x.Copy.Status,
                ScanCodeFor(x.Document, x.Copy),
                x.Copy.CreatedAt));
    }

    public async Task<Result<PagedResult<PrintEventView>>> ListPrintHistoryAsync(
        Guid documentId,
        PagedRequest paging,
        CancellationToken cancellationToken)
    {
        var copies = await distributions.ListForDocumentAsync(documentId, cancellationToken);
        var byId = copies.ToDictionary(c => c.Id, c => c.CopyNumber);

        var events = await distributions.ListPrintEventsAsync(documentId, paging, cancellationToken);

        return Result<PagedResult<PrintEventView>>.Success(
            events
                .Map(e => new PrintEventView(
                    e.Id,
                    e.DistributionId,
                    byId.GetValueOrDefault(e.DistributionId),
                    e.PrintSequence,
                    e.PrintedBy,
                    e.Watermark,
                    e.PrintedAt)));
    }

    private static string ScanCodeFor(ControlledDocument document, DocumentDistribution copy) =>
        ControlledCopyWatermark.ComposeScanCode(document.DocumentNumber, document.Revision, copy.CopyNumber);

    private async Task<Result<(DocumentDistribution Copy, ControlledDocument Document)>> LoadAsync(
        Guid distributionId,
        CancellationToken cancellationToken)
    {
        var copy = await distributions.GetAsync(distributionId, cancellationToken);
        if (copy is null)
        {
            return Error.NotFound("copy_not_found", $"No distributed copy with id {distributionId}.");
        }

        var document = await documents.GetAsync(copy.DocumentId, cancellationToken);
        if (document is null)
        {
            return Error.NotFound("document_not_found", "The document this copy belongs to no longer exists.");
        }

        return Result<(DocumentDistribution Copy, ControlledDocument Document)>.Success((copy, document));
    }

    private async Task<Error?> RequireIssuePermissionAsync(
        ControlledDocument document,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentUser.UserName))
        {
            return Error.Validation("actor_unknown", "The acting user could not be determined.");
        }

        var allowed = await access.HasPermissionAsync(
            Permission.DocumentIssue, document.SiteId, document.DepartmentId, cancellationToken);

        return allowed
            ? null
            : Error.Validation(
                "permission_denied",
                $"{Permission.DocumentIssue} is required for this document's site and department.");
    }
}

/// <param name="IsWatermarked">
/// False when the renderer passed the file through unstamped. Surfaced so a caller never
/// mistakes a plain file for a controlled copy.
/// </param>
public sealed record PrintResult(
    byte[] Content,
    string ContentType,
    bool IsWatermarked,
    string Watermark,
    string ScanCode,
    int PrintSequence,
    int PrintCount,
    int? PrintLimit);
