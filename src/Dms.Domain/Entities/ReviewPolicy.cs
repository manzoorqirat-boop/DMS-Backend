using Dms.Domain.Common;

namespace Dms.Domain.Entities;

/// <summary>
/// How often documents of a type must be re-reviewed, and how far ahead to warn.
/// <para>
/// Master data, resolved most-specific-wins per (type, site) exactly like
/// <see cref="NumberingRule"/> and <see cref="WorkflowDefinition"/> — an SOP may need
/// reviewing every two years while a validation protocol never does, and that varies by
/// customer.
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
        int preIntimationDays,
        string createdBy)
    {
        DocumentTypeId = documentTypeId;
        SiteId = siteId;
        ReviewIntervalMonths = ValidInterval(reviewIntervalMonths);
        PreIntimationDays = ValidPreIntimation(preIntimationDays, ReviewIntervalMonths);
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

    /// <summary>
    /// How many days before the due date a document starts appearing in the "coming due"
    /// report. Long enough that a revision can realistically be drafted, reviewed and approved
    /// before the current version goes overdue — a warning that arrives the week it expires is
    /// not a warning.
    /// </summary>
    public int PreIntimationDays { get; private set; }

    public string CreatedBy { get; private set; } = "";

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    public int Specificity => SiteId is null ? 0 : 1;

    public void Update(int reviewIntervalMonths, int preIntimationDays)
    {
        ReviewIntervalMonths = ValidInterval(reviewIntervalMonths);
        PreIntimationDays = ValidPreIntimation(preIntimationDays, ReviewIntervalMonths);
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

    /// <summary>
    /// Pre-intimation can't exceed the interval itself — a document that starts warning before
    /// its own review period began would be permanently "coming due", which trains people to
    /// ignore the report.
    /// </summary>
    private static int ValidPreIntimation(int days, int intervalMonths)
    {
        if (days < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(days), days, "Pre-intimation days cannot be negative.");
        }

        var intervalDays = intervalMonths * 30;
        return days <= intervalDays
            ? days
            : throw new ArgumentOutOfRangeException(
                nameof(days), days,
                $"Pre-intimation of {days} days exceeds the {intervalMonths}-month review interval.");
    }
}
