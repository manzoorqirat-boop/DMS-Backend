namespace Dms.Domain.Constants;

/// <summary>
/// Content-control tag names every registered template must contain, matched against the
/// <c>&lt;w:tag w:val="..."/&gt;</c> inside each <c>&lt;w:sdt&gt;</c> in the template's
/// <c>word/document.xml</c>. Source: URS Functions #16 — metadata the system auto-populates
/// must not be author-editable, which starts with the template declaring exactly where that
/// metadata goes.
/// <para>
/// These are now the <b>default</b> set, not the only set. A document type with configured
/// <see cref="Dms.Domain.Entities.MetadataFieldDefinition"/> rows uses those instead, with
/// whatever tag names its own templates already carry. This list is what a type falls back to
/// when nothing has been configured — a sensible starting point rather than a placeholder,
/// since these seven are the fields the URS names.
/// </para>
/// <para>
/// Referenced directly only by <c>MetadataFieldService</c>, which owns the fallback. Nothing
/// else should reach for it: consumers ask that service what a type's fields are, so
/// configured and actual can't drift apart.
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

    /// <summary>The default tag set, used when a document type has no fields configured.</summary>
    public static readonly IReadOnlyList<string> Required =
    [
        DocumentNumber, Title, Revision, EffectiveDate, Department, Author, CreatedDate,
    ];
}
