namespace Dms.Domain.Enums;

/// <summary>
/// Lifecycle of a registered <see cref="Dms.Domain.Entities.DocumentTemplate"/>.
/// <para>
/// Linear and one-directional except for the terminal <see cref="Retired"/> state: a
/// template is uploaded, validated once, and either becomes the thing new documents are
/// cloned from or gets fixed and re-uploaded as a new version. There's no "edit in place" —
/// see <see cref="Dms.Domain.Entities.DocumentTemplate"/> for why versions are immutable once created.
/// </para>
/// </summary>
public enum TemplateStatus
{
    /// <summary>Uploaded, not yet run through <see cref="Dms.Domain.Services.DocxTemplateValidator"/>.</summary>
    PendingValidation,

    /// <summary>Passed structural validation; eligible to be made <see cref="Active"/>.</summary>
    ValidationPassed,

    /// <summary>Failed structural validation. See <see cref="Dms.Domain.Entities.DocumentTemplate.ValidationIssues"/> for why.</summary>
    ValidationFailed,

    /// <summary>The live template new documents of this type are cloned from. At most one per DocumentType.</summary>
    Active,

    /// <summary>Superseded by a newer Active version. Documents already created from it keep referencing it by version.</summary>
    Retired,
}
