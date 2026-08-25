using Dms.Domain.Common;

namespace Dms.Domain.Entities;

/// <summary>
/// How often documents of a type must be re-reviewed.
/// <para>
/// Master data, resolved most-specific-wins per (type, site) exactly like
/// <see cref="NumberingRule"/> and <see cref="WorkflowDefinition"/> — an SOP may need
/// reviewing every two years while a validation protocol never does, and that varies by
/// customer.
/// </para>
/// <para>
/// How far ahead to warn is deliberately <b>not</b> here — that lives on the notification rule
/// for the review reminder, where it can differ per reminder kind and be changed without
/// touching the review schedule itself. Two places to configure one lead time is one place too
/// many.
/// </para>
/// <para>
/// The interval drives a due date, not an automatic status change. A document does not stop
/// being effective because its review date passed: withdrawing a procedure people are
/// following, without anyone deciding to, would be worse than the overdue review it was
/// meant to prevent. Overdue documents surface in a report for someone to act on.
/// </para>
/// </summary>
public class ReviewPolicy : Entity, ITimestamped
{
    private ReviewPolicy() { }

    public ReviewPolicy(
        Guid documentTypeId,
        Guid? siteId,
        int reviewIntervalMonths,
        string createdBy)
    {
        DocumentTypeId = documentTypeId;
        SiteId = siteId;
        ReviewIntervalMonths = ValidInterval(reviewIntervalMonths);
        CreatedBy = string.IsNullOrWhiteSpace(createdBy)
            ? throw new ArgumentException("Review policies must be attributable.", nameof(createdBy))
            : createdBy;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid DocumentTypeId { get; private set; }

    /// <summary>Null means this is the default policy for the type, across all sites.</summary>
    public Guid? SiteId { get; private set; }

    /// <summary>Months from effective date to the next required review.</summary>
    public int ReviewIntervalMonths { get; private set; }

    public string CreatedBy { get; private set; } = "";

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    public int Specificity => SiteId is null ? 0 : 1;

    public void Update(int reviewIntervalMonths)
    {
        ReviewIntervalMonths = ValidInterval(reviewIntervalMonths);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Due date for a document that became effective on a given date.
    /// <para>
    /// Measured from the effective date, not from creation or approval: the clock on "is this
    /// still correct" starts when people begin following the document.
    /// </para>
    /// </summary>
    public DateOnly DueDateFrom(DateOnly effectiveDate) =>
        effectiveDate.AddMonths(ReviewIntervalMonths);

    private static int ValidInterval(int months) =>
        months is > 0 and <= 240
            ? months
            : throw new ArgumentOutOfRangeException(
                nameof(months), months, "Review interval must be between 1 and 240 months.");
}
