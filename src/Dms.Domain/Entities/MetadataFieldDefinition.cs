using Dms.Domain.Common;
using Dms.Domain.Enums;

namespace Dms.Domain.Entities;

/// <summary>
/// Declares one system-populated field a document type's template must carry: which
/// <c>&lt;w:tag&gt;</c> the template uses for it, and which piece of DMS data fills it.
/// <para>
/// This replaces the fixed seven-tag list the build started with. The important part is the
/// <b>decoupling</b>: <see cref="Tag"/> is whatever the customer's template already says —
/// <c>SOP_No</c>, <c>DocumentNo</c>, <c>क्रमांक</c> — while <see cref="Source"/> is what DMS
/// puts there. Without that split, onboarding a customer means editing their templates to
/// match our constant names, which is exactly the kind of friction a configurable product
/// shouldn't have.
/// </para>
/// <para>
/// Per document type, so a Protocol can require fields an SOP doesn't. What is <i>not</i>
/// configurable is that a system-populated field is written server-side into a protected
/// region and revalidated on save — an admin can choose which fields exist, not whether they
/// are protected.
/// </para>
/// </summary>
public class MetadataFieldDefinition : Entity, ITimestamped
{
    private MetadataFieldDefinition() { }

    public MetadataFieldDefinition(
        Guid documentTypeId,
        string tag,
        string label,
        MetadataSource source,
        int displayOrder,
        string createdBy)
    {
        DocumentTypeId = documentTypeId;
        Tag = NormalizeTag(tag);
        Label = string.IsNullOrWhiteSpace(label)
            ? throw new ArgumentException("Field label is required.", nameof(label))
            : label.Trim();
        Source = source;
        DisplayOrder = displayOrder;
        CreatedBy = string.IsNullOrWhiteSpace(createdBy)
            ? throw new ArgumentException("Field definitions must be attributable.", nameof(createdBy))
            : createdBy;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid DocumentTypeId { get; private set; }

    /// <summary>
    /// The content-control tag as it appears in the template's <c>word/document.xml</c>.
    /// Matched exactly and case-sensitively, because that is how Word stores it and a
    /// case-insensitive match would let two distinct controls collide.
    /// </summary>
    public string Tag { get; private set; } = "";

    /// <summary>Human-readable name, for the template-validation error an administrator reads.</summary>
    public string Label { get; private set; } = "";

    public MetadataSource Source { get; private set; }

    public int DisplayOrder { get; private set; }

    /// <summary>
    /// Whether a template must contain this control to pass validation.
    /// <para>
    /// Optional fields exist because a customer may want Effective Date printed on an SOP but
    /// not on a form. An optional field that <i>is</i> present is still filled and still
    /// protected — optional governs whether the template must declare it, not whether it's
    /// trusted once declared.
    /// </para>
    /// </summary>
    public bool IsRequired { get; private set; } = true;

    public string CreatedBy { get; private set; } = "";

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    public void Update(string label, MetadataSource source, int displayOrder, bool isRequired)
    {
        Label = string.IsNullOrWhiteSpace(label)
            ? throw new ArgumentException("Field label is required.", nameof(label))
            : label.Trim();
        Source = source;
        DisplayOrder = displayOrder;
        IsRequired = isRequired;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// The tag is deliberately not editable. It is the join between this definition and every
    /// template already registered against the type; changing it would silently invalidate
    /// each of them at the next document creation rather than at the moment of the change.
    /// Retire the definition and add a new one instead.
    /// </summary>
    private static string NormalizeTag(string tag) =>
        string.IsNullOrWhiteSpace(tag)
            ? throw new ArgumentException("Tag is required.", nameof(tag))
            : tag.Trim();
}
