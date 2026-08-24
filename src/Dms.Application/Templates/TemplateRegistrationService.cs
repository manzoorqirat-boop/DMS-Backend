using Dms.Application.Abstractions;
using Dms.Application.Common;
using Dms.Application.Metadata;
using Dms.Domain.Entities;
using Dms.Domain.Enums;
using Dms.Domain.Services;

namespace Dms.Application.Templates;

/// <summary>
/// Phase 1 of the build: register a .docx template against a document type, run it through
/// <see cref="DocxTemplateValidator"/>, and promote a validated version to Active.
/// <para>
/// This service owns the two things a single entity can't: assigning the next
/// <c>TemplateVersion</c> for a type (needs to see siblings) and enforcing "at most one Active
/// template per DocumentType" (spans two aggregates) — both called out explicitly in
/// <see cref="DocumentTemplate"/>'s own remarks as belonging here.
/// </para>
/// </summary>
public sealed class TemplateRegistrationService(
    ITemplateRepository templates,
    IDocumentTypeRepository documentTypes,
    ITemplateFileStore fileStore,
    MetadataFieldService metadataFields,
    IAuditTrail audit,
    ICurrentUser currentUser)
{
    /// <summary>
    /// Hard ceiling on an uploaded template, as a backstop behind the request-size limit
    /// configured at the API edge. A controlled-document template is a few hundred KB of
    /// OOXML; anything approaching this is a mistake or an attempt to exhaust memory.
    /// </summary>
    public const int MaxTemplateBytes = 25 * 1024 * 1024;

    private const string EntityType = "DocumentTemplate";

    private const string TypeVersionIndexFragment = "type_version";
    private const string SingleActiveIndexFragment = "one_active";

    public async Task<Result<TemplateSummary>> RegisterAsync(
        RegisterTemplateRequest request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserName is not { } actor || string.IsNullOrWhiteSpace(actor))
        {
            return Error.Validation(
                "actor_unknown",
                "The acting user could not be determined. Template registration must be attributable.");
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Error.Validation("template_name_required", "A template name is required.");
        }

        if (request.Content.Length == 0)
        {
            return Error.Validation("template_file_empty", "The uploaded template file is empty.");
        }

        if (request.Content.Length > MaxTemplateBytes)
        {
            return Error.Validation(
                "template_file_too_large",
                $"Template exceeds the {MaxTemplateBytes / (1024 * 1024)} MB limit.");
        }

        var documentType = await documentTypes.GetAsync(request.DocumentTypeId, cancellationToken);
        if (documentType is null)
        {
            return Error.NotFound(
                "document_type_not_found",
                $"No document type with id {request.DocumentTypeId}.");
        }

        if (!documentType.IsActive)
        {
            return Error.Validation(
                "document_type_inactive",
                $"Document type '{documentType.Code}' is deactivated; new templates can't be registered against it.");
        }

        var highestVersion = await templates.GetHighestVersionAsync(request.DocumentTypeId, cancellationToken);
        var nextVersion = highestVersion + 1;

        // Keyed off a fresh UUIDv7 rather than the entity id (which the constructor can't be
        // given until the key already exists) or the version number (which a concurrent
        // upload may steal before the insert lands). Opaque and collision-free either way.
        var storageKey = $"templates/{request.DocumentTypeId:N}/{Guid.CreateVersion7():N}.docx";

        // Which controls a template must declare is configuration now, resolved per document
        // type rather than read from a constant.
        var requiredTags = await metadataFields.RequiredTagsForAsync(documentType.Id, cancellationToken);
        var validation = DocxTemplateValidator.Validate(request.Content, requiredTags);

        // Stored even when validation fails: the entity's contract is that a failed template
        // can be uploaded and inspected but never activated, and "inspected" needs the bytes
        // to still be there.
        await fileStore.SaveAsync(storageKey, request.Content, cancellationToken);

        var template = new DocumentTemplate(
            request.DocumentTypeId,
            request.Name.Trim(),
            nextVersion,
            storageKey,
            actor);

        template.RecordValidation(validation.IsValid, validation.Issues);

        templates.Add(template);

        var label = $"{template.Name} v{template.TemplateVersion}";
        audit.Record(AuditAction.TemplateRegistered, EntityType, template.Id, label,
            $"Type {documentType.Code}, {request.Content.Length} bytes.");
        audit.Record(
            validation.IsValid ? AuditAction.TemplateValidationPassed : AuditAction.TemplateValidationFailed,
            EntityType, template.Id, label,
            validation.IsValid ? null : string.Join(" | ", validation.Issues));

        var outcome = await templates.SaveChangesAsync(cancellationToken);

        if (!outcome.Saved)
        {
            // Row never landed, so the blob is an orphan. Best-effort cleanup; a leaked blob
            // is harmless next to a failed request, and the store's Delete is a no-op on a
            // missing key.
            await fileStore.DeleteAsync(storageKey, cancellationToken);

            return outcome.ViolatedIndexContains(TypeVersionIndexFragment)
                ? Error.Conflict(
                    "template_version_conflict",
                    $"Version {nextVersion} for this document type was taken by a concurrent upload. Retry.")
                : Error.Conflict(
                    "template_save_conflict",
                    "The template could not be saved because of a conflicting concurrent change.");
        }

        return TemplateSummary.From(template);
    }

    /// <summary>
    /// Promotes a validated template to Active, retiring whichever version was Active before
    /// it. Both changes flush in one <c>SaveChanges</c>, so the type is never briefly left
    /// with two Active templates or none.
    /// <para>
    /// Idempotent: activating the already-Active template is a no-op success, not a 409 —
    /// a double-clicked admin button shouldn't read as an error.
    /// </para>
    /// </summary>
    public async Task<Result<TemplateSummary>> ActivateAsync(
        Guid templateId,
        CancellationToken cancellationToken)
    {
        var template = await templates.GetAsync(templateId, cancellationToken);
        if (template is null)
        {
            return Error.NotFound("template_not_found", $"No template with id {templateId}.");
        }

        if (template.Status == TemplateStatus.Active)
        {
            return TemplateSummary.From(template);
        }

        if (template.Status != TemplateStatus.ValidationPassed)
        {
            // Checked here rather than letting Activate() throw, so the caller gets a 409 with
            // a reason instead of a 500. The entity's own guard stays as the backstop.
            return Error.Conflict(
                "template_not_activatable",
                $"Template '{template.Name}' v{template.TemplateVersion} is {template.Status}; "
                + $"only a {TemplateStatus.ValidationPassed} template can be activated.");
        }

        var current = await templates.GetActiveAsync(template.DocumentTypeId, cancellationToken);
        if (current is not null && current.Id != template.Id)
        {
            current.Retire();
            audit.Record(
                AuditAction.TemplateRetired, EntityType, current.Id,
                $"{current.Name} v{current.TemplateVersion}",
                $"Superseded by v{template.TemplateVersion}.");
        }

        template.Activate();
        audit.Record(
            AuditAction.TemplateActivated, EntityType, template.Id,
            $"{template.Name} v{template.TemplateVersion}",
            current is null ? "First active template for this type." : $"Replaced v{current.TemplateVersion}.");

        var outcome = await templates.SaveChangesAsync(cancellationToken);
        if (!outcome.Saved)
        {
            return outcome.ViolatedIndexContains(SingleActiveIndexFragment)
                ? Error.Conflict(
                    "template_activation_conflict",
                    "Another template for this document type was activated concurrently. Reload and retry.")
                : Error.Conflict(
                    "template_save_conflict",
                    "The template could not be activated because of a conflicting concurrent change.");
        }

        return TemplateSummary.From(template);
    }

    /// <summary>
    /// Retires a template outright. Retiring the Active version deliberately leaves the type
    /// with no template — that blocks new document creation for it, which is the correct
    /// behaviour when a type is being withdrawn, and is recoverable by activating another
    /// version.
    /// </summary>
    public async Task<Result<TemplateSummary>> RetireAsync(
        Guid templateId,
        CancellationToken cancellationToken)
    {
        var template = await templates.GetAsync(templateId, cancellationToken);
        if (template is null)
        {
            return Error.NotFound("template_not_found", $"No template with id {templateId}.");
        }

        if (template.Status == TemplateStatus.Retired)
        {
            return TemplateSummary.From(template);
        }

        template.Retire();
        audit.Record(
            AuditAction.TemplateRetired, EntityType, template.Id,
            $"{template.Name} v{template.TemplateVersion}", "Retired directly by an administrator.");

        var outcome = await templates.SaveChangesAsync(cancellationToken);
        return outcome.Saved
            ? TemplateSummary.From(template)
            : Error.Conflict(
                "template_save_conflict",
                "The template could not be retired because of a conflicting concurrent change.");
    }

    public async Task<Result<TemplateSummary>> GetAsync(Guid templateId, CancellationToken cancellationToken)
    {
        var template = await templates.GetAsync(templateId, cancellationToken);
        return template is null
            ? Error.NotFound("template_not_found", $"No template with id {templateId}.")
            : TemplateSummary.From(template);
    }

    public async Task<IReadOnlyList<TemplateSummary>> ListAsync(
        Guid? documentTypeId,
        CancellationToken cancellationToken)
    {
        var found = await templates.ListAsync(documentTypeId, cancellationToken);
        return found.Select(TemplateSummary.From).ToList();
    }

    /// <summary>
    /// The raw .docx of a registered template, for an admin to inspect a failed validation or
    /// for the document server to open a working copy from. Returns the bytes plus a
    /// suggested filename; the storage key itself never leaves this layer.
    /// </summary>
    public async Task<Result<(byte[] Content, string FileName)>> DownloadAsync(
        Guid templateId,
        CancellationToken cancellationToken)
    {
        var template = await templates.GetAsync(templateId, cancellationToken);
        if (template is null)
        {
            return Error.NotFound("template_not_found", $"No template with id {templateId}.");
        }

        var content = await fileStore.ReadAsync(template.StorageKey, cancellationToken);
        if (content is null)
        {
            // Row exists, blob doesn't — a real integrity problem worth surfacing plainly
            // rather than returning an empty file.
            return Error.NotFound(
                "template_file_missing",
                $"The stored file for template '{template.Name}' v{template.TemplateVersion} is missing.");
        }

        // Constructed explicitly rather than relying on an implicit conversion from a tuple
        // literal — the conversion would have to chain tuple-literal then user-defined,
        // which is exactly the kind of thing that compiles on one C# version and not another.
        return Result<(byte[] Content, string FileName)>.Success((content, BuildFileName(template)));
    }

    /// <summary>
    /// Builds a download filename from a user-supplied template name. Everything outside
    /// [A-Za-z0-9-_] is collapsed to an underscore rather than escaped: the name reaches a
    /// Content-Disposition header, and quotes, newlines, or path separators there are a
    /// response-splitting and path-traversal surface. The version suffix guarantees the result
    /// is never empty even if the name is entirely non-ASCII.
    /// </summary>
    private static string BuildFileName(DocumentTemplate template)
    {
        var sanitized = new string(template.Name
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_')
            .ToArray())
            .Trim('_');

        return $"{sanitized}_v{template.TemplateVersion}.docx";
    }
}
