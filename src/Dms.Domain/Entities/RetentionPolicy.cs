using Dms.Domain.Common;
using Dms.Domain.Enums;

namespace Dms.Domain.Entities;

/// <summary>
/// How long records of a document type are kept after they leave active use, and from what
/// event the clock runs.
/// <para>
/// Master data with the same most-specific-wins resolution as numbering, workflow and review
/// policies. Retention periods are set by regulation, product type and market — a batch-record
/// SOP and an IT procedure at the same site legitimately differ by decades.
/// </para>
/// </summary>
public class RetentionPolicy : Entity, ITimestamped
{
    private RetentionPolicy() { }

    public RetentionPolicy(
        Guid documentTypeId,
        Guid? siteId,
        int retentionYears,
        RetentionTrigger trigger,
        string createdBy)
    {
        DocumentTypeId = documentTypeId;
        SiteId = siteId;
        RetentionYears = ValidYears(retentionYears);
        Trigger = trigger;
        CreatedBy = string.IsNullOrWhiteSpace(createdBy)
            ? throw new ArgumentException("Retention policies must be attributable.", nameof(createdBy))
            : createdBy;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid DocumentTypeId { get; private set; }

    /// <summary>Null means this is the default policy for the type, across all sites.</summary>
    public Guid? SiteId { get; private set; }

    public int RetentionYears { get; private set; }

    public RetentionTrigger Trigger { get; private set; }

    public string CreatedBy { get; private set; } = "";

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    public int Specificity => SiteId is null ? 0 : 1;

    public void Update(int retentionYears, RetentionTrigger trigger)
    {
        RetentionYears = ValidYears(retentionYears);
        Trigger = trigger;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// The date a record becomes eligible for disposition, counted from the triggering event.
    /// </summary>
    public DateOnly RetainUntil(DateOnly triggeredOn) => triggeredOn.AddYears(RetentionYears);

    /// <summary>
    /// One year minimum, hundred maximum. Zero would make a record disposable the moment it
    /// left use, which no retention schedule permits and which would most likely be a typo
    /// rather than a decision.
    /// </summary>
    private static int ValidYears(int years) =>
        years is > 0 and <= 100
            ? years
            : throw new ArgumentOutOfRangeException(
                nameof(years), years, "Retention must be between 1 and 100 years.");
}
