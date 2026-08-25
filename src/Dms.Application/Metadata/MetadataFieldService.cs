using Dms.Application.Abstractions;
using Dms.Application.Common;
using Dms.Domain.Constants;
using Dms.Domain.Entities;
using Dms.Domain.Enums;

namespace Dms.Application.Metadata;

/// <summary>
/// Administers per-document-type metadata field definitions, and is the single place that
/// resolves them for a document.
/// <para>
/// Every consumer — template validation, draft creation, integrity verification — goes through
/// <see cref="ResolveForTypeAsync"/> and <see cref="RequiredTagsForAsync"/> rather than
/// reaching for <see cref="TemplateFieldTags"/> directly, so a type's configured fields and the
/// fields actually written and checked can't drift apart.
/// </para>
/// </summary>
public sealed class MetadataFieldService(
    IMetadataFieldRepository fields,
    IDocumentTypeRepository documentTypes,
    IAccessControl access,
    IAuditTrail audit,
    ICurrentUser currentUser)
{
    private const string EntityType = "MetadataFieldDefinition";

    /// <summary>
    /// A type's configured fields, or the built-in default set when it has none.
    /// <para>
    /// Falling back rather than failing keeps the system usable before anything is configured,
    /// and the default is the seven fields the URS names — a sensible starting point, not a
    /// placeholder. Configuring even one field replaces the default set entirely, because a
    /// half-merged mix of configured and default fields is far harder to reason about than
    /// either alone.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<MetadataFieldDefinition>> ResolveForTypeAsync(
        Guid documentTypeId,
        CancellationToken cancellationToken)
    {
        var configured = await fields.ListForTypeAsync(documentTypeId, cancellationToken);

        return configured.Count > 0
            ? configured
            : DefaultFieldsFor(documentTypeId);
    }

    /// <summary>Tags a template for this type must declare to pass validation.</summary>
    public async Task<IReadOnlyList<string>> RequiredTagsForAsync(
        Guid documentTypeId,
        CancellationToken cancellationToken)
    {
        var configured = await ResolveForTypeAsync(documentTypeId, cancellationToken);

        return configured
            .Where(f => f.IsRequired)
            .Select(f => f.Tag)
            .ToList();
    }

    public async Task<Result<MetadataFieldView>> AddAsync(
        CreateMetadataFieldRequest request,
        CancellationToken cancellationToken)
    {
        var gate = await RequireConfigureAsync(cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var documentType = await documentTypes.GetAsync(request.DocumentTypeId, cancellationToken);
        if (documentType is null)
        {
            return Error.NotFound("document_type_not_found", $"No document type with id {request.DocumentTypeId}.");
        }

        MetadataFieldDefinition field;
        try
        {
            field = new MetadataFieldDefinition(
                request.DocumentTypeId,
                request.Tag,
                request.Label,
                request.Source,
                request.DisplayOrder,
                currentUser.UserName!);
        }
        catch (ArgumentException ex)
        {
            return Error.Validation("metadata_field_invalid", ex.Message);
        }

        fields.Add(field);
        audit.Record(
            AuditAction.MetadataFieldAdded, EntityType, field.Id,
            $"{documentType.Code}/{field.Tag}",
            $"Source {field.Source}, {(request.IsRequired ? "required" : "optional")}.");

        var outcome = await fields.SaveChangesAsync(cancellationToken);
        if (!outcome.Saved)
        {
            return outcome.ViolatedIndexContains("tag")
                ? Error.Conflict(
                    "metadata_tag_taken",
                    $"Document type '{documentType.Code}' already defines a field for tag '{field.Tag}'.")
                : Error.Conflict("metadata_field_save_conflict", "The field could not be saved.");
        }

        return MetadataFieldView.From(field);
    }

    public async Task<Result<MetadataFieldView>> UpdateAsync(
        Guid fieldId,
        UpdateMetadataFieldRequest request,
        CancellationToken cancellationToken)
    {
        var gate = await RequireConfigureAsync(cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var field = await fields.GetAsync(fieldId, cancellationToken);
        if (field is null)
        {
            return Error.NotFound("metadata_field_not_found", $"No metadata field with id {fieldId}.");
        }

        var previousSource = field.Source;

        try
        {
            field.Update(request.Label, request.Source, request.DisplayOrder, request.IsRequired);
        }
        catch (ArgumentException ex)
        {
            return Error.Validation("metadata_field_invalid", ex.Message);
        }

        audit.Record(
            AuditAction.MetadataFieldChanged, EntityType, field.Id, field.Tag,
            previousSource == field.Source
                ? $"Label/order updated. Source remains {field.Source}."
                : $"Source {previousSource} → {field.Source}. Documents already created keep the values written at the time.");

        var outcome = await fields.SaveChangesAsync(cancellationToken);
        return outcome.Saved
            ? MetadataFieldView.From(field)
            : Error.Conflict("metadata_field_save_conflict", "The field could not be updated.");
    }

    /// <summary>
    /// Removes a field definition.
    /// <para>
    /// Templates already registered against the type keep their content control; it simply
    /// stops being filled and stops being checked. Documents already created keep whatever was
    /// written into them, which is correct — their content is a record, not a projection of
    /// current configuration.
    /// </para>
    /// </summary>
    public async Task<Result<bool>> RemoveAsync(Guid fieldId, CancellationToken cancellationToken)
    {
        var gate = await RequireConfigureAsync(cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var field = await fields.GetAsync(fieldId, cancellationToken);
        if (field is null)
        {
            return Error.NotFound("metadata_field_not_found", $"No metadata field with id {fieldId}.");
        }

        fields.Remove(field);
        audit.Record(
            AuditAction.MetadataFieldRemoved, EntityType, field.Id, field.Tag,
            $"Source was {field.Source}. Existing documents are unchanged.");

        var outcome = await fields.SaveChangesAsync(cancellationToken);
        return outcome.Saved
            ? Result<bool>.Success(true)
            : Error.Conflict("metadata_field_save_conflict", "The field could not be removed.");
    }

    public async Task<IReadOnlyList<MetadataFieldView>> ListAsync(
        Guid documentTypeId,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveForTypeAsync(documentTypeId, cancellationToken);
        return resolved.Select(MetadataFieldView.From).ToList();
    }

    /// <summary>
    /// The seven URS-named fields, as unsaved definitions. Constructed rather than persisted so
    /// that "no configuration" stays genuinely empty in the database — seeding rows would make
    /// a customer who never configured anything indistinguishable from one who deliberately
    /// chose these exact seven.
    /// </summary>
    private static List<MetadataFieldDefinition> DefaultFieldsFor(Guid documentTypeId) =>
    [
        new(documentTypeId, TemplateFieldTags.DocumentNumber, "Document Number", MetadataSource.DocumentNumber, 1, "system"),
        new(documentTypeId, TemplateFieldTags.Title, "Title", MetadataSource.DocumentTitle, 2, "system"),
        new(documentTypeId, TemplateFieldTags.Revision, "Revision", MetadataSource.Revision, 3, "system"),
        new(documentTypeId, TemplateFieldTags.EffectiveDate, "Effective Date", MetadataSource.EffectiveDate, 4, "system"),
        new(documentTypeId, TemplateFieldTags.Department, "Department", MetadataSource.DepartmentName, 5, "system"),
        new(documentTypeId, TemplateFieldTags.Author, "Author", MetadataSource.Author, 6, "system"),
        new(documentTypeId, TemplateFieldTags.CreatedDate, "Created Date", MetadataSource.CreatedDate, 7, "system"),
    ];

    private async Task<Error?> RequireConfigureAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentUser.UserName))
        {
            return Error.Validation("actor_unknown", "The acting user could not be determined.");
        }

        // Global scope: a metadata field definition applies to a document type everywhere, so
        // a site-scoped grant would be granting authority over other sites' documents.
        var allowed = await access.HasPermissionAsync(
            Permission.WorkflowConfigure, siteId: null, departmentId: null, cancellationToken);

        return allowed
            ? null
            : Error.Validation(
                "permission_denied",
                $"{Permission.WorkflowConfigure} at organisation-wide scope is required to configure metadata fields.");
    }
}
