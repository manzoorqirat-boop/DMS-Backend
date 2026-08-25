using Dms.Domain.Common;
using Dms.Domain.Enums;

namespace Dms.Domain.Entities;

/// <summary>
/// One issued copy of an effective document — who holds it, which copy number it is, and
/// whether it has come back.
/// <para>
/// The register of outstanding copies is the point. When a document is superseded or
/// withdrawn, someone has to physically collect every controlled copy in circulation, and
/// they can only do that if the system knows where each one went. A DMS that issues copies
/// without tracking them has automated the easy half of distribution control.
/// </para>
/// </summary>
public class DocumentDistribution : Entity, ITimestamped
{
    private DocumentDistribution() { }

    public DocumentDistribution(
        Guid documentId,
        int copyNumber,
        CopyType copyType,
        Guid? issuedToDepartmentId,
        string issuedToName,
        string issuedBy,
        int? printLimit)
    {
        DocumentId = documentId;
        CopyNumber = copyNumber > 0
            ? copyNumber
            : throw new ArgumentOutOfRangeException(nameof(copyNumber), "Copy numbers start at 1.");
        CopyType = copyType;
        IssuedToDepartmentId = issuedToDepartmentId;
        IssuedToName = RequireNonEmpty(issuedToName, nameof(issuedToName));
        IssuedBy = RequireNonEmpty(issuedBy, nameof(issuedBy));

        PrintLimit = printLimit switch
        {
            null => null,
            <= 0 => throw new ArgumentOutOfRangeException(
                nameof(printLimit), printLimit, "A print limit must be positive, or null for unlimited."),
            _ => printLimit,
        };

        Status = DistributionStatus.Issued;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid DocumentId { get; private set; }

    /// <summary>
    /// Sequential per document, starting at 1. Printed on the copy itself, and the thing a
    /// retrieval checklist is ticked against.
    /// </summary>
    public int CopyNumber { get; private set; }

    public CopyType CopyType { get; private set; }

    public Guid? IssuedToDepartmentId { get; private set; }

    /// <summary>
    /// Who holds it, as recorded at issue — a department name, a person, an external auditor.
    /// Denormalised deliberately: a copy issued to "QA" in 2024 was issued to QA as it was
    /// then, and renaming the department later must not rewrite the distribution record.
    /// </summary>
    public string IssuedToName { get; private set; } = "";

    public string IssuedBy { get; private set; } = "";

    public DistributionStatus Status { get; private set; }

    /// <summary>
    /// How many times this copy may be printed. Null means unlimited, which is only sensible
    /// for an uncontrolled copy — a controlled copy that can be reprinted without limit isn't
    /// a controlled copy.
    /// </summary>
    public int? PrintLimit { get; private set; }

    public int PrintCount { get; private set; }

    public DateTimeOffset? AcknowledgedAt { get; private set; }
    public string? AcknowledgedBy { get; private set; }

    public DateTimeOffset? ReturnedAt { get; private set; }
    public string? ReturnedBy { get; private set; }

    /// <summary>Why a copy was written off rather than returned — lost, destroyed on site.</summary>
    public string? ClosureNote { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    /// <summary>Still in circulation and therefore still owed back.</summary>
    public bool IsOutstanding => Status is DistributionStatus.Issued or DistributionStatus.Acknowledged;

    public bool CanPrint => IsOutstanding && (PrintLimit is null || PrintCount < PrintLimit);

    /// <summary>
    /// The recipient confirms they received it. Distinct from issue: a copy that was sent but
    /// never arrived is exactly the gap a distribution record exists to expose.
    /// </summary>
    public void Acknowledge(string acknowledgedBy)
    {
        if (Status != DistributionStatus.Issued)
        {
            throw new InvalidOperationException(
                $"Copy {CopyNumber} is {Status}; only an {DistributionStatus.Issued} copy can be acknowledged.");
        }

        AcknowledgedBy = RequireNonEmpty(acknowledgedBy, nameof(acknowledgedBy));
        AcknowledgedAt = DateTimeOffset.UtcNow;
        Status = DistributionStatus.Acknowledged;
        Touch();
    }

    /// <summary>Physically collected back. The normal end for a controlled copy.</summary>
    public void Retrieve(string retrievedBy)
    {
        if (!IsOutstanding)
        {
            throw new InvalidOperationException($"Copy {CopyNumber} is already {Status}.");
        }

        ReturnedBy = RequireNonEmpty(retrievedBy, nameof(retrievedBy));
        ReturnedAt = DateTimeOffset.UtcNow;
        Status = DistributionStatus.Retrieved;
        Touch();
    }

    /// <summary>
    /// Closes out a copy that can't be returned. Requires a note, because "we couldn't find
    /// copy 3" is a finding and needs to read as one rather than quietly matching the count.
    /// </summary>
    public void CloseOut(DistributionStatus outcome, string note, string closedBy)
    {
        if (outcome is not (DistributionStatus.Destroyed or DistributionStatus.Lost))
        {
            throw new ArgumentException(
                $"{outcome} is not a close-out outcome; use Retrieve for a returned copy.", nameof(outcome));
        }

        if (!IsOutstanding)
        {
            throw new InvalidOperationException($"Copy {CopyNumber} is already {Status}.");
        }

        ClosureNote = RequireNonEmpty(note, nameof(note));
        ReturnedBy = RequireNonEmpty(closedBy, nameof(closedBy));
        ReturnedAt = DateTimeOffset.UtcNow;
        Status = outcome;
        Touch();
    }

    /// <summary>
    /// Increments the print counter. Returns the new print sequence number, which goes onto
    /// the printed page — so a copy found in the field can be traced to the exact print event
    /// that produced it, not merely to the copy record.
    /// </summary>
    public int RecordPrint()
    {
        if (!IsOutstanding)
        {
            throw new InvalidOperationException($"Copy {CopyNumber} is {Status} and cannot be reprinted.");
        }

        if (PrintLimit is { } limit && PrintCount >= limit)
        {
            throw new InvalidOperationException(
                $"Copy {CopyNumber} has reached its print limit of {limit}.");
        }

        PrintCount++;
        Touch();
        return PrintCount;
    }

    private void Touch() => UpdatedAt = DateTimeOffset.UtcNow;

    private static string RequireNonEmpty(string value, string paramName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{paramName} is required.", paramName)
            : value.Trim();
}

/// <summary>
/// One print of one controlled copy. Append-only, like the audit trail and electronic
/// signatures — the count on <see cref="DocumentDistribution"/> is a running total, and this
/// is the itemised record behind it.
/// </summary>
public class PrintEvent : Entity
{
    private PrintEvent() { }

    public PrintEvent(
        Guid distributionId,
        Guid documentId,
        int printSequence,
        string printedBy,
        string watermark)
    {
        DistributionId = distributionId;
        DocumentId = documentId;
        PrintSequence = printSequence;
        PrintedBy = string.IsNullOrWhiteSpace(printedBy)
            ? throw new ArgumentException("Prints must be attributable.", nameof(printedBy))
            : printedBy;
        Watermark = watermark;
        PrintedAt = DateTimeOffset.UtcNow;
    }

    public Guid DistributionId { get; private set; }

    /// <summary>Denormalised so print history can be queried per document without a join.</summary>
    public Guid DocumentId { get; private set; }

    /// <summary>1 for the first print of this copy, 2 for the reprint, and so on.</summary>
    public int PrintSequence { get; private set; }

    public string PrintedBy { get; private set; } = "";

    /// <summary>
    /// The exact text stamped on the page. Stored rather than recomputed: if the watermark
    /// format changes later, a page recovered from a filing cabinet still has to be
    /// reconcilable against what the system says was printed that day.
    /// </summary>
    public string Watermark { get; private set; } = "";

    public DateTimeOffset PrintedAt { get; private set; }
}
