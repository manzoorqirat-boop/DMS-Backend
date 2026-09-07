using Dms.Application.Abstractions;
using Dms.Application.Common;
using Dms.Domain.Entities;
using Dms.Domain.Enums;

namespace Dms.Application.Documents;

/// <summary>
/// A site's adoption of a document issued centrally by another site.
/// <para>
/// The adopting site takes the document verbatim and has no authority to alter it, so an
/// adoption records acceptance rather than producing a copy. One document, many adoptions, and
/// no way for a site's text to drift from the master.
/// </para>
/// </summary>
public sealed class AdoptionService(
    IDocumentAdoptionRepository adoptions,
    IControlledDocumentRepository documents,
    IAccessControl access,
    IAuditTrail audit,
    ICurrentUser currentUser,
    IClock clock)
{
    private const string EntityType = "DocumentAdoption";

    public async Task<Result<AdoptionView>> AdoptAsync(
        Guid documentId,
        AdoptDocumentRequest request,
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

        // Permission is checked at the ADOPTING site, not the document's owning site. A site's
        // QA accepts documents for their own site and has no rights over corporate's — checking
        // there would deny exactly the people who should be doing this.
        var permitted = await access.HasPermissionAsync(
            Permission.DocumentIssue, request.SiteId, departmentId: null, cancellationToken);

        if (!permitted)
        {
            return Error.Validation(
                "permission_denied",
                $"{Permission.DocumentIssue} is required at the adopting site.");
        }

        if (!document.CanBeAdoptedBy(request.SiteId))
        {
            return Error.Conflict("not_adoptable", ExplainRefusal(document, request.SiteId));
        }

        if (await adoptions.HasActiveAdoptionAsync(document.Id, request.SiteId, cancellationToken))
        {
            return Error.Conflict(
                "already_adopted", $"This site has already adopted {document.DocumentNumber}.");
        }

        DocumentAdoption adoption;
        try
        {
            adoption = new DocumentAdoption(
                document.Id, request.SiteId, request.EffectiveDate, clock.Today, actor, request.Note);
        }
        catch (ArgumentException ex)
        {
            return Error.Validation("adoption_invalid", ex.Message);
        }

        adoptions.Add(adoption);

        audit.Record(
            AuditAction.DocumentMadeEffective, EntityType, adoption.Id,
            $"{document.DocumentNumber} Rev {document.Revision:00}",
            $"Adopted for local use, effective {request.EffectiveDate:yyyy-MM-dd}."
            + (request.Note is null ? "" : $" {request.Note}"));

        var outcome = await adoptions.SaveChangesAsync(cancellationToken);

        return outcome.Saved
            ? AdoptionView.From(adoption, document)
            : Error.Conflict("adoption_save_conflict", "The adoption could not be recorded.");
    }

    /// <summary>
    /// One refusal message covering several causes, each phrased for what the caller most likely
    /// did. The alternative is four near-identical error codes that all mean "you can't".
    /// </summary>
    private static string ExplainRefusal(ControlledDocument document, Guid siteId) =>
        document.Scope != DocumentScope.Global
            ? $"{document.DocumentNumber} is a local document and cannot be adopted by another site."
            : document.SiteId == siteId
                ? $"{document.DocumentNumber} belongs to this site and is already in force here "
                  + "through its own lifecycle."
                : $"{document.DocumentNumber} is {document.Status}"
                  + (document.IsCurrentRevision ? "" : " and not the current revision")
                  + ". Only the effective, current revision of a global document can be adopted.";

    public async Task<Result<AdoptionView>> WithdrawAsync(
        Guid adoptionId,
        WithdrawAdoptionRequest request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserName is not { } actor || string.IsNullOrWhiteSpace(actor))
        {
            return Error.Validation("actor_unknown", "The acting user could not be determined.");
        }

        var adoption = await adoptions.GetAsync(adoptionId, cancellationToken);
        if (adoption is null)
        {
            return Error.NotFound("adoption_not_found", "That adoption no longer exists.");
        }

        var permitted = await access.HasPermissionAsync(
            Permission.DocumentIssue, adoption.SiteId, departmentId: null, cancellationToken);

        if (!permitted)
        {
            return Error.Validation(
                "permission_denied",
                $"{Permission.DocumentIssue} is required at the adopting site.");
        }

        var document = await documents.GetAsync(adoption.DocumentId, cancellationToken);

        try
        {
            adoption.Withdraw(actor, request.Reason);
        }
        catch (InvalidOperationException ex)
        {
            return Error.Conflict("adoption_already_withdrawn", ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Error.Validation("reason_required", ex.Message);
        }

        audit.Record(
            AuditAction.DocumentObsoleted, EntityType, adoption.Id,
            document?.DocumentNumber ?? adoption.DocumentId.ToString(),
            $"Adoption withdrawn. Reason: {request.Reason}");

        var outcome = await adoptions.SaveChangesAsync(cancellationToken);

        return outcome.Saved
            ? AdoptionView.From(adoption, document)
            : Error.Conflict("adoption_save_conflict", "The withdrawal could not be recorded.");
    }

    public async Task<Result<IReadOnlyList<AdoptionView>>> ListForDocumentAsync(
        Guid documentId,
        CancellationToken cancellationToken)
    {
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
                "permission_denied", $"{Permission.DocumentView} is required for this document.");
        }

        var found = await adoptions.ListForDocumentAsync(documentId, cancellationToken);

        return Result<IReadOnlyList<AdoptionView>>.Success(
            found.Select(a => AdoptionView.From(a, document)).ToList());
    }

    /// <summary>Global documents a site could adopt but has not — its adoption worklist.</summary>
    public async Task<Result<IReadOnlyList<DocumentSummary>>> ListAdoptableAsync(
        Guid siteId,
        CancellationToken cancellationToken)
    {
        var permitted = await access.HasPermissionAsync(
            Permission.DocumentView, siteId, departmentId: null, cancellationToken);

        if (!permitted)
        {
            return Error.Validation(
                "permission_denied", $"{Permission.DocumentView} is required at this site.");
        }

        var found = await adoptions.ListAdoptableForSiteAsync(siteId, cancellationToken);

        return Result<IReadOnlyList<DocumentSummary>>.Success(
            found.Select(DocumentSummary.From).ToList());
    }
}
