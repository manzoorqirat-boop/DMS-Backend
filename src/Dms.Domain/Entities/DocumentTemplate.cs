using Dms.Domain.Common;
using Dms.Domain.Enums;

namespace Dms.Domain.Entities;

/// <summary>
/// A registered .docx template for a <see cref="DocumentType"/>. Source: URS Functions #4
/// (locked header/footer/heading regions) and #16 (author cannot edit system-populated
/// metadata).
/// <para>
/// The template file itself lives in the document store, keyed by <see cref="StorageKey"/>
/// — this entity is the governance record: which version is active, whether it passed
/// structural validation, and who registered it. A template that fails
/// <see cref="Dms.Domain.Services.DocxTemplateValidator"/> can be uploaded and inspected but can never
/// be <see cref="Activate"/>d, so a malformed template can't silently become the thing every
/// new document of that type gets cloned from.
/// </para>
/// <para>
/// Versions are immutable once created — there's no "edit an existing template" here, only
/// register a new version and activate it. That's what lets an in-flight document keep
/// referencing the exact template it was created under: activating v3 doesn't rewrite v2 out
/// from under a draft an author already has open.
/// </para>
/// <para>
/// "Only one Active template per DocumentType" is a cross-aggregate invariant and is
/// deliberately not enforced here — it belongs to the application service that activates
/// templates (query the existing Active one for this DocumentType, retire it, then activate
/// the new one, inside one transaction), not to a single entity that can't see its siblings.
/// </para>
/// </summary>
public class DocumentTemplate : Entity, ITimestamped
{
    private DocumentTemplate() { }

    public DocumentTemplate(
        Guid documentTypeId, string name, int templateVersion, string storageKey, string createdBy)
    {
        DocumentTypeId = documentTypeId;
        Name = RequireNonEmpty(name, nameof(name));
        TemplateVersion = templateVersion > 0
            ? templateVersion
            : throw new ArgumentOutOfRangeException(nameof(templateVersion), "Template version must be positive.");
        StorageKey = RequireNonEmpty(storageKey, nameof(storageKey));
        CreatedBy = RequireNonEmpty(createdBy, nameof(createdBy));
        Status = TemplateStatus.PendingValidation;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid DocumentTypeId { get; private set; }
    public string Name { get; private set; } = "";

    /// <summary>Monotonically increasing per <see cref="DocumentTypeId"/>. Assigned by the application service — this entity doesn't know its own siblings, so it can't compute the next number itself.</summary>
    public int TemplateVersion { get; private set; }

    /// <summary>
    /// Key into the document store for the raw .docx bytes. Never a client-facing download
    /// path — the whole point of registering templates this way is that the author's editing
    /// session opens a working copy server-side, never this file directly.
    /// </summary>
    public string StorageKey { get; private set; } = "";

    public string CreatedBy { get; private set; } = "";
    public TemplateStatus Status { get; private set; } = TemplateStatus.PendingValidation;

    public DateTimeOffset? ValidatedAt { get; private set; }

    /// <summary>
    /// Snapshot of the last validation run's findings — empty when it passed clean. Kept even
    /// on a pass, not just on failure, so "what did the validator actually check" stays
    /// answerable later without re-running it against a template that may since be Retired.
    /// </summary>
    public IReadOnlyList<string> ValidationIssues { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    /// <summary>An author's editing session may only be opened against an Active template.</summary>
    public bool IsUsable => Status == TemplateStatus.Active;

    public void RecordValidation(bool passed, IReadOnlyList<string> issues)
    {
        ValidatedAt = DateTimeOffset.UtcNow;
        ValidationIssues = issues;
        Status = passed ? TemplateStatus.ValidationPassed : TemplateStatus.ValidationFailed;
        Touch();
    }

    /// <summary>
    /// Promotes a validated template to Active. Throws rather than silently no-op'ing on a
    /// template that never passed validation — an admin clicking "Activate" on a failed
    /// template is a mistake to surface, not an instruction to skip the check.
    /// </summary>
    public void Activate()
    {
        if (Status != TemplateStatus.ValidationPassed)
        {
            throw new InvalidOperationException(
                $"Cannot activate template '{Name}' v{TemplateVersion}: status is {Status}, " +
                $"not {TemplateStatus.ValidationPassed}.");
        }

        Status = TemplateStatus.Active;
        Touch();
    }

    public void Retire()
    {
        Status = TemplateStatus.Retired;
        Touch();
    }

    private void Touch() => UpdatedAt = DateTimeOffset.UtcNow;

    private static string RequireNonEmpty(string value, string paramName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{paramName} is required.", paramName)
            : value;
}
