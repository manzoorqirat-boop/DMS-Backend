namespace Dms.Domain.Constants;

/// <summary>
/// Content-control tag names every registered template must contain, matched against the
/// <c>&lt;w:tag w:val="..."/&gt;</c> inside each <c>&lt;w:sdt&gt;</c> in the template's
/// <c>word/document.xml</c>. Source: URS Functions #16 — metadata the system auto-populates
/// must not be author-editable, which starts with the template declaring exactly where that
/// metadata goes.
/// <para>
/// Deliberately one fixed set for every <see cref="Dms.Domain.Entities.DocumentType"/> rather than a
/// per-type configurable list — Doc No / Title / Revision / Effective Date / Department /
/// Author / Created Date are the fields the numbering and workflow services fill in
/// regardless of whether the document is an SOP or a Protocol. A type-specific extension
/// point can be added later if a real template needs one; nothing here forecloses it.
/// </para>
/// </summary>
public static class TemplateFieldTags
{
    public const string DocumentNumber = "DocNo";
    public const string Title = "Title";
    public const string Revision = "Revision";
    public const string EffectiveDate = "EffectiveDate";
    public const string Department = "Department";
    public const string Author = "Author";
    public const string CreatedDate = "CreatedDate";

    /// <summary>Every tag a template must declare to pass structural validation.</summary>
    public static readonly IReadOnlyList<string> Required =
    [
        DocumentNumber, Title, Revision, EffectiveDate, Department, Author, CreatedDate,
    ];
}
