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

/// <summary>
/// Lifecycle of a <see cref="Dms.Domain.Entities.ControlledDocument"/>, as described by the URS:
/// initiation, review, approval, issuance, distribution, retrieval, revision, obsolescence.
/// <para>
/// The full set is declared now because the states are URS-driven and known, but only the
/// Draft-stage transitions are implemented — review and approval are ERES's to drive
/// (Phase 5), and writing speculative transition methods for them would mean rewriting them
/// once the real envelope callbacks exist.
/// </para>
/// </summary>
public enum DocumentStatus
{
    /// <summary>Created from a template, being authored. The only editable state.</summary>
    Draft,

    /// <summary>Handed to ERES as an envelope; awaiting reviewer sign-off.</summary>
    InReview,

    /// <summary>All approvers signed. Not yet in force.</summary>
    Approved,

    /// <summary>In force from its effective date. The version a controlled copy prints from.</summary>
    Effective,

    /// <summary>Replaced by a later revision that is now Effective.</summary>
    Superseded,

    /// <summary>Withdrawn from use with no replacement. Retained for the retention period.</summary>
    Obsolete,

    /// <summary>Abandoned before ever being issued. The number stays burned rather than reused.</summary>
    Withdrawn,
}
