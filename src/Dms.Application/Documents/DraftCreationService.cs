using System.Globalization;
using Dms.Application.Abstractions;
using Dms.Application.Metadata;
using Dms.Application.Numbering;
using Dms.Application.Common;
using Dms.Domain.Entities;
using Dms.Domain.Enums;
using Dms.Domain.Services;
using Dms.Domain.Common;

namespace Dms.Application.Documents;

/// <summary>
/// Phase 2: creates a controlled document in Draft from the Active template for its type.
/// <para>
/// The sequence of events matters and is the reason this is one service rather than several:
/// a number is allocated, the Active template is fetched and its content controls filled with
/// that number and the rest of the system metadata, the resulting working copy is stored, and
/// only then does the register row get written. The number allocation and the register insert
/// share a transaction so that a failure anywhere after allocation returns the number rather
/// than leaving a hole in the register.
/// </para>
/// </summary>
public sealed class DraftCreationService(
    IControlledDocumentRepository documents,
    ISiteRepository sites,
    IDepartmentRepository departments,
    IDocumentTypeRepository documentTypes,
    ITemplateRepository templates,
    ITemplateFileStore templateFiles,
    IDocumentFileStore documentFiles,
    IUnitOfWork unitOfWork,
    NumberingRuleService numbering,
    MetadataFieldService metadataFields,
    IAccessControl access,
    IAuditTrail audit,
    ICurrentUser currentUser)
{
    private const string EntityType = "ControlledDocument";

    /// <summary>
    /// Creates an annexure under a parent document.
    /// <para>
    /// <b>Draft parents only.</b> An annexure added to a document already in force would be new
    /// controlled content entering force without having passed a signature route — and because
    /// an annexure is never separately approvable, there is no route it could pass on its own.
    /// Adding one to an effective SOP means revising the SOP.
    /// </para>
    /// <para>
    /// The annexure gets its own template, chosen by document type: a cleaning-record form
    /// looks nothing like the procedure it belongs to, so inheriting the parent's template
    /// would produce a form with an SOP's structure.
    /// </para>
    /// </summary>
    public async Task<Result<DocumentSummary>> CreateAnnexureAsync(
        Guid parentDocumentId,
        CreateAnnexureRequest request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserName is not { } author || string.IsNullOrWhiteSpace(author))
        {
            return Error.Validation("actor_unknown", "The acting user could not be determined.");
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return Error.Validation("document_title_required", "An annexure title is required.");
        }

        var parent = await documents.GetAsync(parentDocumentId, cancellationToken);
        if (parent is null)
        {
            return Error.NotFound("document_not_found", $"No document with id {parentDocumentId}.");
        }

        if (parent.IsAnnexure)
        {
            return Error.Validation(
                "annexure_nesting",
                $"{parent.DocumentNumber} is itself an annexure. Annexures cannot be nested.");
        }

        if (parent.Status != DocumentStatus.Draft)
        {
            return Error.Conflict(
                "parent_not_draft",
                $"Annexures can only be added while the parent is a Draft. {parent.DocumentNumber} "
                + $"is {parent.Status} — revise it to add an annexure, so the new content passes "
                + "a signature route.");
        }

        var permitted = await access.HasPermissionAsync(
            Permission.DocumentCreate, parent.SiteId, parent.DepartmentId, cancellationToken);

        if (!permitted)
        {
            return Error.Validation(
                "permission_denied",
                $"{Permission.DocumentCreate} is required for this site and department.");
        }

        var documentType = await documentTypes.GetAsync(request.DocumentTypeId, cancellationToken);
        if (documentType is null || !documentType.IsActive)
        {
            return Error.NotFound(
                "document_type_not_found", "The chosen document type does not exist or is inactive.");
        }

        var template = await templates.GetActiveAsync(documentType.Id, cancellationToken);
        if (template is null)
        {
            return Error.Conflict(
                "no_active_template",
                $"'{documentType.Code}' has no active template to create an annexure from.");
        }

        var templateBytes = await templateFiles.ReadAsync(template.StorageKey, cancellationToken);
        if (templateBytes is null)
        {
            return Error.NotFound(
                "template_file_missing",
                $"The stored file for the active template of '{documentType.Code}' is missing.");
        }

        var site = await sites.GetAsync(parent.SiteId, cancellationToken);
        var department = await departments.GetAsync(parent.DepartmentId, cancellationToken);
        if (site is null || department is null)
        {
            return Error.Conflict(
                "master_data_unresolved",
                "The parent document's site or department could not be resolved.");
        }

        var existing = await documents.ListAnnexuresAsync(parent.Id, cancellationToken);

        // Highest-plus-one rather than count-plus-one: an annexure that was withdrawn leaves a
        // gap, and reusing its number would put two documents in the register that had once
        // carried the same identity.
        var nextNumber = existing.Count == 0
            ? 1
            : existing.Max(a => a.AnnexureNumber ?? 0) + 1;

        var fieldDefinitions = await metadataFields.ResolveForTypeAsync(documentType.Id, cancellationToken);
        var workingCopyKey = $"documents/{Uuid7.NewGuid():N}.docx";

        try
        {
            // No sequence allocation: the number is derived from the parent's, so there is no
            // shared counter to contend over and no transaction needed to protect one.
            var annexure = ControlledDocument.CreateAnnexure(
                parent, nextNumber, request.Title, template.Id, workingCopyKey, author);

            var merge = DocxMetadataWriter.Write(
                templateBytes,
                MetadataResolver.Resolve(
                    fieldDefinitions,
                    BuildContext(annexure, site, department, documentType, author)));

            if (merge.MissingTags.Count > 0)
            {
                throw new DraftAbortedException(Error.Conflict(
                    "template_fields_missing",
                    $"The active template is missing content control(s): {string.Join(", ", merge.MissingTags)}. "
                    + "Re-register and re-validate the template."));
            }

            await documentFiles.SaveAsync(workingCopyKey, merge.Content, cancellationToken);

            documents.Add(annexure);
            audit.Record(
                AuditAction.DocumentCreated, EntityType, annexure.Id, annexure.DocumentNumber,
                $"Annexure {nextNumber} of {parent.DocumentNumber}; title '{annexure.Title}'; "
                + $"type {documentType.Code}; template {template.Name} v{template.TemplateVersion}.");

            var outcome = await documents.SaveChangesAsync(cancellationToken);

            if (!outcome.Saved)
            {
                throw new DraftAbortedException(Error.Conflict(
                    "annexure_save_conflict",
                    "The annexure could not be created because of a conflicting concurrent change. "
                    + "Another annexure may have been added at the same moment."));
            }

            return DocumentSummary.From(annexure);
        }
        catch (DraftAbortedException ex)
        {
            await documentFiles.DeleteAsync(workingCopyKey, CancellationToken.None);
            return ex.Error;
        }
        catch
        {
            await documentFiles.DeleteAsync(workingCopyKey, CancellationToken.None);
            throw;
        }
    }

    /// <summary>A document's annexures, in order.</summary>
    public async Task<Result<IReadOnlyList<DocumentSummary>>> ListAnnexuresAsync(
        Guid parentDocumentId,
        CancellationToken cancellationToken)
    {
        var parent = await documents.GetAsync(parentDocumentId, cancellationToken);
        if (parent is null)
        {
            return Error.NotFound("document_not_found", $"No document with id {parentDocumentId}.");
        }

        var permitted = await access.HasPermissionAsync(
            Permission.DocumentView, parent.SiteId, parent.DepartmentId, cancellationToken);

        if (!permitted)
        {
            return Error.Validation(
                "permission_denied",
                $"{Permission.DocumentView} is required for this document's site and department.");
        }

        var annexures = await documents.ListAnnexuresAsync(parent.Id, cancellationToken);

        return Result<IReadOnlyList<DocumentSummary>>.Success(
            annexures.Select(DocumentSummary.From).ToList());
    }

    public async Task<Result<DocumentSummary>> CreateDraftAsync(
        CreateDraftRequest request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserName is not { } author || string.IsNullOrWhiteSpace(author))
        {
            return Error.Validation(
                "actor_unknown",
                "The acting user could not be determined. Document creation must be attributable.");
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return Error.Validation("document_title_required", "A document title is required.");
        }

        // Checked against the document's own site and department rather than globally: an
        // author authorised in QA at one plant has no business creating documents in
        // Production at another.
        var permitted = await access.HasPermissionAsync(
            Permission.DocumentCreate, request.SiteId, request.DepartmentId, cancellationToken);

        if (!permitted)
        {
            return Error.Validation(
                "permission_denied",
                $"{Permission.DocumentCreate} is required for this site and department.");
        }

        var context = await ResolveContextAsync(request, cancellationToken);
        if (!context.IsSuccess)
        {
            return context.Error!;
        }

        var (site, department, documentType, template) = context.Value;

        var templateBytes = await templateFiles.ReadAsync(template.StorageKey, cancellationToken);
        if (templateBytes is null)
        {
            return Error.NotFound(
                "template_file_missing",
                $"The stored file for the active template of '{documentType.Code}' is missing.");
        }

        var workingCopyKey = $"documents/{Uuid7.NewGuid():N}.docx";

        // Pattern resolution reads master data, so it happens before the transaction opens —
        // there's no reason to hold the sequence row lock while looking up configuration.
        var fieldDefinitions = await metadataFields.ResolveForTypeAsync(documentType.Id, cancellationToken);
        var pattern = await numbering.ResolvePatternAsync(documentType.Id, site.Id, cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var periodKey = DocumentNumberPattern.PeriodKeyFor(pattern, today);

        try
        {
            return await unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                var sequence = await documents.AllocateNextSequenceAsync(
                    site.Id, department.Id, documentType.Id, periodKey, ct);

                var documentNumber = DocumentNumberPattern.Render(
                    pattern,
                    new NumberTokens(
                        site.Code, department.Code, documentType.Code,
                        sequence, Revision: 0, today));

                var document = new ControlledDocument(
                    documentNumber,
                    request.Title,
                    site.Id,
                    department.Id,
                    documentType.Id,
                    template.Id,
                    workingCopyKey,
                    author);

                var merge = DocxMetadataWriter.Write(
                    templateBytes,
                    MetadataResolver.Resolve(
                        fieldDefinitions,
                        BuildContext(document, site, department, documentType, author)));

                if (merge.MissingTags.Count > 0)
                {
                    // The template passed validation when it was registered, so its stored
                    // bytes and its validation record have since diverged. Failing here beats
                    // issuing a controlled document with blank metadata on its face.
                    throw new DraftAbortedException(Error.Conflict(
                        "template_fields_missing",
                        $"The active template is missing content control(s): {string.Join(", ", merge.MissingTags)}. "
                        + "Re-register and re-validate the template."));
                }

                await documentFiles.SaveAsync(workingCopyKey, merge.Content, ct);

                documents.Add(document);
                audit.Record(
                    AuditAction.DocumentCreated, EntityType, document.Id, document.DocumentNumber,
                    $"Title '{document.Title}'; type {documentType.Code}; "
                    + $"template {template.Name} v{template.TemplateVersion}.");

                var outcome = await documents.SaveChangesAsync(ct);

                if (!outcome.Saved)
                {
                    throw new DraftAbortedException(
                        outcome.ViolatedIndexContains("title")
                            ? Error.Conflict(
                                "document_title_taken",
                                $"A document titled '{request.Title}' already exists for type '{documentType.Code}'.")
                            : Error.Conflict(
                                "document_save_conflict",
                                "The document could not be created because of a conflicting concurrent change."));
                }

                return DocumentSummary.From(document);
            }, cancellationToken);
        }
        catch (DraftAbortedException ex)
        {
            // Transaction rolled back, so the sequence number was returned rather than burned.
            // The blob store isn't transactional, so clean up the orphaned working copy.
            await documentFiles.DeleteAsync(workingCopyKey, CancellationToken.None);
            return ex.Error;
        }
        catch
        {
            await documentFiles.DeleteAsync(workingCopyKey, CancellationToken.None);
            throw;
        }
    }

    public async Task<Result<DocumentSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var document = await documents.GetAsync(id, cancellationToken);
        return document is null
            ? Error.NotFound("document_not_found", $"No document with id {id}.")
            : DocumentSummary.From(document);
    }

    /// <param name="currentRevisionsOnly">
    /// True gives the master list — one row per document, showing the revision in force.
    /// False includes superseded revisions, which is the register view.
    /// </param>
    public async Task<PagedResult<DocumentSummary>> ListAsync(
        Guid? siteId,
        Guid? departmentId,
        Guid? documentTypeId,
        bool currentRevisionsOnly,
        bool includeAnnexures,
        string? search,
        DocumentStatus? status,
        PagedRequest paging,
        CancellationToken cancellationToken)
    {
        var found = await documents.ListAsync(
            siteId, departmentId, documentTypeId, currentRevisionsOnly, includeAnnexures, search,
            status, paging, cancellationToken);

        return found.Map(DocumentSummary.From);
    }

    public async Task<Result<DocumentSummary>> WithdrawAsync(Guid id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentUser.UserName))
        {
            return Error.Validation(
                "actor_unknown",
                "The acting user could not be determined. Withdrawal must be attributable.");
        }

        var document = await documents.GetAsync(id, cancellationToken);
        if (document is null)
        {
            return Error.NotFound("document_not_found", $"No document with id {id}.");
        }

        try
        {
            document.Withdraw();
        }
        catch (InvalidOperationException ex)
        {
            return Error.Conflict("document_not_withdrawable", ex.Message);
        }

        audit.Record(
            AuditAction.DocumentWithdrawn, EntityType, document.Id, document.DocumentNumber,
            "Draft abandoned; number remains issued and is not reused.");

        var outcome = await documents.SaveChangesAsync(cancellationToken);
        return outcome.Saved
            ? DocumentSummary.From(document)
            : Error.Conflict(
                "document_save_conflict",
                "The document could not be withdrawn because of a conflicting concurrent change.");
    }

    /// <summary>
    /// The working copy as stored. Until the document server lands in Phase 3 this is how a
    /// draft is inspected; it is not the author's editing path, and won't become one — URS
    /// Functions #13 forbids the real file reaching a client PC.
    /// </summary>
    public async Task<Result<(byte[] Content, string FileName)>> DownloadWorkingCopyAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var document = await documents.GetAsync(id, cancellationToken);
        if (document is null)
        {
            return Error.NotFound("document_not_found", $"No document with id {id}.");
        }

        var content = await documentFiles.ReadAsync(document.WorkingCopyKey, cancellationToken);
        if (content is null)
        {
            return Error.NotFound(
                "document_file_missing",
                $"The working copy for {document.DocumentNumber} is missing.");
        }

        return Result<(byte[] Content, string FileName)>.Success(
            (content, $"{document.DocumentNumber}.docx"));
    }

    /// <summary>
    /// Re-checks a stored working copy against the metadata the server wrote into it, and
    /// against its own document protection.
    /// <para>
    /// Once Phase 3 lands, the document server's save callback calls this before accepting a
    /// save, and a failure rejects the write. Exposed as an explicit operation now so the
    /// check is testable, and so an administrator can re-verify a document already at rest —
    /// worth having permanently, not just as a stand-in.
    /// </para>
    /// </summary>
    public async Task<Result<IntegrityCheckResult>> VerifyWorkingCopyAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentUser.UserName))
        {
            return Error.Validation(
                "actor_unknown",
                "The acting user could not be determined. Integrity checks must be attributable.");
        }

        var document = await documents.GetAsync(id, cancellationToken);
        if (document is null)
        {
            return Error.NotFound("document_not_found", $"No document with id {id}.");
        }

        var department = await departments.GetAsync(document.DepartmentId, cancellationToken);
        if (department is null)
        {
            return Error.NotFound("department_not_found", "The document's department no longer exists.");
        }

        var site = await sites.GetAsync(document.SiteId, cancellationToken);
        var documentType = await documentTypes.GetAsync(document.DocumentTypeId, cancellationToken);
        if (site is null || documentType is null)
        {
            return Error.NotFound(
                "document_context_missing",
                "The document's site or type no longer exists, so its metadata can't be recomputed.");
        }

        var content = await documentFiles.ReadAsync(document.WorkingCopyKey, cancellationToken);
        if (content is null)
        {
            return Error.NotFound(
                "document_file_missing",
                $"The working copy for {document.DocumentNumber} is missing.");
        }

        // Same definitions and same resolver the writer used. Building the expected map any
        // other way here would make a formatting difference look like tampering.
        var fieldDefinitions = await metadataFields.ResolveForTypeAsync(
            document.DocumentTypeId, cancellationToken);

        var verification = DocxProtectionVerifier.Verify(
            content,
            MetadataResolver.Resolve(
                fieldDefinitions,
                BuildContext(document, site, department, documentType, document.Author)));

        audit.Record(
            verification.IsValid
                ? AuditAction.DocumentIntegrityCheckPassed
                : AuditAction.DocumentIntegrityCheckFailed,
            EntityType, document.Id, document.DocumentNumber,
            verification.IsValid ? null : string.Join(" | ", verification.Findings));

        // Recorded whether it passed or failed. A trail that only shows failures can't
        // demonstrate that checking happens at all, which is half of what an inspector is
        // asking when they ask whether it happens.
        var outcome = await documents.SaveChangesAsync(cancellationToken);
        if (!outcome.Saved)
        {
            return Error.Conflict(
                "audit_save_conflict",
                "The integrity check completed but its audit record could not be written.");
        }

        return new IntegrityCheckResult(document.DocumentNumber, verification.IsValid, verification.Findings);
    }

    /// <summary>
    /// Loads and checks everything the draft depends on before any of it is written to. Each
    /// failure names the specific thing that's wrong rather than a generic "invalid request" —
    /// "department is deactivated" and "no active template for this type" send an admin to
    /// completely different places.
    /// </summary>
    private async Task<Result<(Site Site, Department Department, DocumentType Type, DocumentTemplate Template)>>
        ResolveContextAsync(CreateDraftRequest request, CancellationToken cancellationToken)
    {
        var site = await sites.GetAsync(request.SiteId, cancellationToken);
        if (site is null)
        {
            return Error.NotFound("site_not_found", $"No site with id {request.SiteId}.");
        }

        if (!site.IsActive)
        {
            return Error.Validation("site_inactive", $"Site '{site.Code}' is deactivated.");
        }

        var department = await departments.GetAsync(request.DepartmentId, cancellationToken);
        if (department is null)
        {
            return Error.NotFound("department_not_found", $"No department with id {request.DepartmentId}.");
        }

        if (department.SiteId != site.Id)
        {
            return Error.Validation(
                "department_site_mismatch",
                $"Department '{department.Code}' does not belong to site '{site.Code}'.");
        }

        if (!department.IsActive)
        {
            return Error.Validation("department_inactive", $"Department '{department.Code}' is deactivated.");
        }

        var documentType = await documentTypes.GetAsync(request.DocumentTypeId, cancellationToken);
        if (documentType is null)
        {
            return Error.NotFound("document_type_not_found", $"No document type with id {request.DocumentTypeId}.");
        }

        if (!documentType.IsActive)
        {
            return Error.Validation("document_type_inactive", $"Document type '{documentType.Code}' is deactivated.");
        }

        var template = await templates.GetActiveAsync(documentType.Id, cancellationToken);
        if (template is null)
        {
            return Error.Conflict(
                "no_active_template",
                $"Document type '{documentType.Code}' has no active template. Register and activate one first.");
        }

        return Result<(Site Site, Department Department, DocumentType Type, DocumentTemplate Template)>.Success(
            (site, department, documentType, template));
    }

    /// <summary>
    /// The seven system-populated fields, keyed by the tag names the template declares. Dates
    /// are written in ISO form: a controlled document read across sites shouldn't depend on the
    /// reader's locale to disambiguate 03/04 — and EffectiveDate is deliberately blank on a
    /// draft rather than guessed at, since nothing is effective until it's approved.
    /// </summary>
    /// <summary>
    /// Flattens the document and its master data into the shape <see cref="MetadataResolver"/>
    /// consumes. Author full name falls back to the username when the user record can't be
    /// read — a blank name on a controlled document is worse than a less friendly one.
    /// </summary>
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
