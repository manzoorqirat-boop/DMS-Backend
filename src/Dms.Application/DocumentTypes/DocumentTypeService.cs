using Dms.Application.Abstractions;
using Dms.Application.Common;
using Dms.Domain.Entities;

namespace Dms.Application.DocumentTypes;

/// <summary>
/// Master-data maintenance for document types. Thin by design, matching
/// <see cref="DocumentType"/> itself — review-level counts, site scoping and numbering
/// patterns belong to the numbering/workflow phase, not here.
/// <para>
/// Included in Phase 1 only because a template has to be registered <i>against</i> something:
/// without at least create-and-list, the template endpoints can't be exercised end to end.
/// </para>
/// </summary>
public sealed class DocumentTypeService(IDocumentTypeRepository repository, ICurrentUser currentUser)
{
    private const string CodeIndexFragment = "code";

    public async Task<Result<DocumentTypeSummary>> CreateAsync(
        CreateDocumentTypeRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentUser.UserName))
        {
            return Error.Validation(
                "actor_unknown",
                "The acting user could not be determined. Master-data changes must be attributable.");
        }

        DocumentType documentType;
        try
        {
            documentType = new DocumentType(request.Code, request.Name);
        }
        catch (ArgumentException ex)
        {
            // The entity's own guards are the single source of truth for what a valid code or
            // name is; re-implementing them here would be a second definition to drift.
            return Error.Validation("document_type_invalid", ex.Message);
        }

        repository.Add(documentType);
        var outcome = await repository.SaveChangesAsync(cancellationToken);

        if (!outcome.Saved)
        {
            return outcome.ViolatedIndexContains(CodeIndexFragment)
                ? Error.Conflict(
                    "document_type_code_taken",
                    $"A document type with code '{documentType.Code}' already exists.")
                : Error.Conflict(
                    "document_type_save_conflict",
                    "The document type could not be saved because of a conflicting concurrent change.");
        }

        return DocumentTypeSummary.From(documentType);
    }

    public async Task<IReadOnlyList<DocumentTypeSummary>> ListAsync(
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var found = await repository.ListAsync(includeInactive, cancellationToken);
        return found.Select(DocumentTypeSummary.From).ToList();
    }

    public async Task<Result<DocumentTypeSummary>> SetActiveAsync(
        Guid id,
        bool isActive,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentUser.UserName))
        {
            return Error.Validation(
                "actor_unknown",
                "The acting user could not be determined. Master-data changes must be attributable.");
        }

        var documentType = await repository.GetAsync(id, cancellationToken);
        if (documentType is null)
        {
            return Error.NotFound("document_type_not_found", $"No document type with id {id}.");
        }

        if (isActive)
        {
            documentType.Reactivate();
        }
        else
        {
            documentType.Deactivate();
        }

        var outcome = await repository.SaveChangesAsync(cancellationToken);
        return outcome.Saved
            ? DocumentTypeSummary.From(documentType)
            : Error.Conflict(
                "document_type_save_conflict",
                "The document type could not be updated because of a conflicting concurrent change.");
    }
}
