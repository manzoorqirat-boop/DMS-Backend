using Dms.Application.Abstractions;
using Dms.Application.Common;
using Dms.Domain.Entities;
using Dms.Domain.Enums;
using Dms.Domain.Services;

namespace Dms.Application.Documents;

/// <summary>
/// Produces the PDF rendition of an approved document, with its signature manifest appended.
/// <para>
/// This is the artefact that actually leaves the system — the thing filed, distributed and
/// handed to an inspector. A .docx is the authoring format; a PDF is flattened, cannot be
/// reopened and silently edited the way a Word file can, and carries the signature manifest
/// on its own final page so the export is self-contained.
/// </para>
/// <para>
/// <b>Generated on demand, not at the moment of approval.</b> Approval is the regulated act:
/// the last signature is applied, the content hash is frozen, the audit entry is written. If
/// PDF generation were part of that transaction, a document server hiccup would fail an
/// approval that had otherwise validly occurred — turning a rendering problem into a
/// compliance one. So the PDF is built the first time it is asked for and cached thereafter.
/// </para>
/// <para>
/// The storage key is derived by convention rather than stored on the document, which avoids
/// adding a column to a schema created by EnsureCreated (where no migration path exists to
/// apply one). The tradeoff is that "has a PDF been generated" is answered by probing storage
/// rather than reading a flag — acceptable because the probe is a single blob read, and the
/// answer is only needed on the download path.
/// </para>
/// </summary>
public sealed class ApprovedPdfService(
    IControlledDocumentRepository documents,
    ISignatureRepository signatures,
    IDocumentFileStore documentFiles,
    IDocumentConverter converter,
    IAccessControl access,
    IAuditTrail audit,
    ICurrentUser currentUser)
{
    private const string EntityType = "ControlledDocument";

    /// <summary>Deterministic from the document and revision — see the class remarks.</summary>
    public static string StorageKeyFor(ControlledDocument document) =>
        $"approved-pdf/{document.Id:N}-r{document.Revision}.pdf";

    public async Task<Result<(byte[] Content, string FileName)>> GetOrCreateAsync(
        Guid documentId,
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

        var permitted = await access.HasPermissionAsync(
            Permission.DocumentView, document.SiteId, document.DepartmentId, cancellationToken);

        if (!permitted)
        {
            return Error.Validation(
                "permission_denied",
                $"{Permission.DocumentView} is required for this document's site and department.");
        }

        // Only an approved document has a frozen artefact to render. A draft's content changes
        // under you, so a PDF of it would be a snapshot of nothing in particular — and its
        // signature manifest would be empty, which is worse than absent.
        if (document.ApprovedCopyKey is not { Length: > 0 } approvedKey)
        {
            return Error.Conflict(
                "not_approved",
                $"{document.DocumentNumber} has not been approved, so it has no approved PDF. "
                + "A PDF rendition is produced once every signature on the route is applied.");
        }

        var fileName = $"{document.DocumentNumber}-r{document.Revision:00}.pdf";
        var pdfKey = StorageKeyFor(document);

        // Cached from a previous request. The approved .docx is immutable once frozen, so a
        // PDF built from it never needs invalidating — a revision produces a new document row
        // with its own key.
        var cached = await documentFiles.ReadAsync(pdfKey, cancellationToken);
        if (cached is { Length: > 0 })
        {
            return Result<(byte[], string)>.Success((cached, fileName));
        }

        if (!converter.IsAvailable)
        {
            return Error.Conflict(
                "converter_not_configured",
                "No document server is configured, so the approved PDF cannot be produced. "
                + "The approved Word file remains available and is unaffected.");
        }

        var approvedDocx = await documentFiles.ReadAsync(approvedKey, cancellationToken);
        if (approvedDocx is null)
        {
            return Error.NotFound(
                "approved_file_missing",
                $"The stored approved file for {document.DocumentNumber} is missing.");
        }

        var applied = await signatures.GetSignaturesAsync(document.Id, cancellationToken);
        var withManifest = SignaturePageBuilder.Append(approvedDocx, document, applied);

        byte[] pdf;
        try
        {
            pdf = await converter.ToPdfAsync(withManifest, cancellationToken);
        }
        catch (Exception ex)
        {
            // Surfaced rather than swallowed: a caller that asked for a PDF and received a
            // .docx would store it under a .pdf name and only discover it on opening.
            return Error.Conflict(
                "conversion_failed",
                $"The approved PDF could not be produced: {ex.Message}");
        }

        await documentFiles.SaveAsync(pdfKey, pdf, cancellationToken);

        audit.Record(
            AuditAction.DocumentApproved, EntityType, document.Id, document.DocumentNumber,
            $"Approved PDF rendition generated with {applied.Count} signature(s) on the manifest.");

        await documents.SaveChangesAsync(cancellationToken);

        return Result<(byte[], string)>.Success((pdf, fileName));
    }
}
