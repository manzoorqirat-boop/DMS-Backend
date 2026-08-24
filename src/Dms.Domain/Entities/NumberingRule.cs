using Dms.Domain.Common;
using Dms.Domain.Services;

namespace Dms.Domain.Entities;

/// <summary>
/// The numbering pattern to use for a document type, optionally overridden per site.
/// <para>
/// Resolution is most-specific-wins: a rule naming both the type and the site beats one naming
/// only the type, which beats <see cref="DocumentNumberPattern.Default"/>. That ordering lets a
/// company set one convention centrally and let an acquired site keep its own without either
/// being a special case in code.
/// </para>
/// <para>
/// The pattern is validated on construction and on every change, so an invalid pattern can
/// never be persisted — the failure surfaces to the administrator saving the rule rather than
/// to the author who happens to create the next document.
/// </para>
/// </summary>
public class NumberingRule : Entity, ITimestamped
{
    private NumberingRule() { }

    public NumberingRule(Guid documentTypeId, Guid? siteId, string pattern, string createdBy)
    {
        DocumentTypeId = documentTypeId;
        SiteId = siteId;
        Pattern = ValidatedPattern(pattern);
        CreatedBy = string.IsNullOrWhiteSpace(createdBy)
            ? throw new ArgumentException("Numbering rules must be attributable.", nameof(createdBy))
            : createdBy;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid DocumentTypeId { get; private set; }

    /// <summary>Null means this is the default rule for the type, across all sites.</summary>
    public Guid? SiteId { get; private set; }

    public string Pattern { get; private set; } = "";

    public string CreatedBy { get; private set; } = "";

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    /// <summary>
    /// Changes the pattern for documents created from now on.
    /// <para>
    /// Numbers already issued are untouched and are never regenerated — a controlled document's
    /// number is its identity, printed on paper copies and cited in other documents, and
    /// retrospectively renumbering a register is not a supported operation in any regulated
    /// system. A pattern change means old and new documents of the same type will look
    /// different, which is expected and correct.
    /// </para>
    /// </summary>
    public void ChangePattern(string pattern)
    {
        Pattern = ValidatedPattern(pattern);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>How specific this rule is. Higher wins when several rules match.</summary>
    public int Specificity => SiteId is null ? 0 : 1;

    private static string ValidatedPattern(string pattern)
    {
        var result = DocumentNumberPattern.Validate(pattern);

        return result.IsValid
            ? pattern.Trim()
            : throw new ArgumentException(
                $"Invalid numbering pattern: {string.Join(" ", result.Issues)}", nameof(pattern));
    }
}
