using Dms.Application.Abstractions;
using Dms.Application.Common;
using Dms.Application.Metadata;
using Dms.Domain.Entities;
using Dms.Domain.Enums;
using Dms.Domain.Services;

namespace Dms.Application.Documents;

/// <summary>
/// The revision cycle: opening revision <i>n+1</i> of a document already in force, and
/// reading a document's full revision history.
/// <para>
/// A revision is a new record sharing the predecessor's document number and lineage, not an
/// edit of it. The version that was effective keeps its content, its signatures and the hash
/// binding them — which is what lets anyone answer "what did this SOP actually say in March"
/// two years later.
/// </para>
/// </summary>
public sealed class DocumentRevisionService(
    IControlledDocumentRepository documents,
    ISiteRepository sites,
    IDepartmentRepository departments,
    IDocumentTypeRepository documentTypes,
    ITemplateRepository templates,
    ITemplateFileStore templateFiles,
    IDocumentFileStore documentFiles,
    MetadataFieldService metadataFields,
    IAccessControl access,
    IAuditTrail audit,
    ICurrentUser currentUser)
{
    private const string EntityType = "ControlledDocument";

    /// <summary>
    /// Opens the next revision as a fresh Draft.
    /// <para>
    /// The new draft is built from the document type's <b>currently active template</b>, not
    /// from a copy of the predecessor's file. That's the consequential choice here: a revision
    /// should pick up whatever the approved template now looks like — a changed header, a new
    /// mandated footer — rather than perpetuating a template version that may since have been
    /// retired for a reason. The author re-enters the body content, which is also the point at
    /// which a revision gets reviewed rather than rubber-stamped.
    /// </para>
    /// </summary>
    public async Task<Result<DocumentSummary>> BeginRevisionAsync(
        Guid documentId,
        string reason,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserName is not { } author || string.IsNullOrWhiteSpace(author))
        {
            return Error.Validation(
                "actor_unknown",
                "The acting user could not be determined. Revisions must be attributable.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            // A revision without a stated reason is the thing an inspector asks about first.
            return Error.Validation(
                "revision_reason_required",
                "State why the document is being revised.");
        }

        var source = await documents.GetAsync(documentId, cancellationToken);
        if (source is null)
        {
            return Error.NotFound("document_not_found", $"No document with id {documentId}.");
        }

        var permitted = await access.HasPermissionAsync(
            Permission.DocumentCreate, source.SiteId, source.DepartmentId, cancellationToken);

        if (!permitted)
        {
            return Error.Validation(
                "permission_denied",
                $"{Permission.DocumentCreate} is required for this document's site and department.");
        }

        // Checked before the entity guard so the caller gets a specific message: "someone else
        // is already revising this" is a different problem from "this version isn't in force".
        var inFlight = await documents.GetInFlightRevisionAsync(source.FamilyId, cancellationToken);
        if (inFlight is not null)
        {
            return Error.Conflict(
                "revision_already_open",
                $"Revision {inFlight.Revision} of {inFlight.DocumentNumber} is already open "
                + $"({inFlight.Status}). Complete or withdraw it before starting another.");
        }

        var context = await LoadContextAsync(source, cancellationToken);
        if (!context.IsSuccess)
        {
            return context.Error!;
        }

        var (site, department, documentType) = context.Value;

        var template = await templates.GetActiveAsync(source.DocumentTypeId, cancellationToken);
        if (template is null)
        {
            return Error.Conflict(
                "no_active_template",
                $"Document type '{documentType.Code}' has no active template, so a revision can't be started.");
        }

        var templateBytes = await templateFiles.ReadAsync(template.StorageKey, cancellationToken);
        if (templateBytes is null)
        {
            return Error.NotFound(
                "template_file_missing",
                $"The stored file for the active template of '{documentType.Code}' is missing.");
        }

        var workingCopyKey = $"documents/{Guid.CreateVersion7():N}.docx";

        ControlledDocument revision;
        try
        {
            revision = source.BeginRevision(workingCopyKey, author);
        }
        catch (InvalidOperationException ex)
        {
            return Error.Conflict("document_not_revisable", ex.Message);
        }

        var fieldDefinitions = await metadataFields.ResolveForTypeAsync(
            source.DocumentTypeId, cancellationToken);

        var merge = DocxMetadataWriter.Write(
            templateBytes,
            MetadataResolver.Resolve(fieldDefinitions, BuildContext(revision, site, department, documentType, author)));

        if (merge.MissingTags.Count > 0)
        {
            return Error.Conflict(
                "template_fields_missing",
                $"The active template is missing content control(s): {string.Join(", ", merge.MissingTags)}.");
        }

        await documentFiles.SaveAsync(workingCopyKey, merge.Content, cancellationToken);

        documents.Add(revision);
        audit.Record(
            AuditAction.DocumentRevisionStarted, EntityType, revision.Id,
            $"{revision.DocumentNumber} Rev {revision.Revision:00}",
            $"Revised from Rev {source.Revision:00}. Reason: {reason.Trim()}");

        var outcome = await documents.SaveChangesAsync(cancellationToken);
        if (!outcome.Saved)
        {
            await documentFiles.DeleteAsync(workingCopyKey, CancellationToken.None);

            return outcome.ViolatedIndexContains("family_revision")
                ? Error.Conflict(
                    "revision_already_open",
                    "Another revision was started concurrently. Reload and retry.")
                : Error.Conflict("document_save_conflict", "The revision could not be created.");
        }

        return DocumentSummary.From(revision);
    }

    /// <summary>
    /// Every revision of a document, oldest first — the version history an inspector asks for.
    /// Accepts any revision's id and resolves the whole lineage from it.
    /// </summary>
    public async Task<Result<IReadOnlyList<DocumentSummary>>> ListRevisionsAsync(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var document = await documents.GetAsync(documentId, cancellationToken);
        if (document is null)
        {
            return Error.NotFound("document_not_found", $"No document with id {documentId}.");
        }

        var family = await documents.ListFamilyAsync(document.FamilyId, cancellationToken);
        return Result<IReadOnlyList<DocumentSummary>>.Success(
            family.Select(DocumentSummary.From).ToList());
    }

    private async Task<Result<(Site Site, Department Department, DocumentType Type)>> LoadContextAsync(
        ControlledDocument document,
        CancellationToken cancellationToken)
    {
        var site = await sites.GetAsync(document.SiteId, cancellationToken);
        var department = await departments.GetAsync(document.DepartmentId, cancellationToken);
        var documentType = await documentTypes.GetAsync(document.DocumentTypeId, cancellationToken);

        if (site is null || department is null || documentType is null)
        {
            return Error.NotFound(
                "document_context_missing",
                "The document's site, department or type no longer exists.");
        }

        return Result<(Site Site, Department Department, DocumentType Type)>.Success(
            (site, department, documentType));
    }

    private static MetadataContext BuildContext(
        ControlledDocument document,
        Site site,
        Department department,
        DocumentType documentType,
        string author) =>
        new(
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
            author,
            author,
            document.CreatedAt,
            document.Status);
}
