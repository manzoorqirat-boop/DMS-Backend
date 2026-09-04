using Dms.Domain.Entities;
using Dms.Domain.Enums;

namespace Dms.Application.Distribution;

public sealed record IssueCopyRequest(
    CopyType CopyType,
    Guid? IssuedToDepartmentId,
    string IssuedToName,
    int? PrintLimit);

public sealed record DistributionView(
    Guid Id,
    Guid DocumentId,
    int CopyNumber,
    CopyType CopyType,
    string IssuedToName,
    string IssuedBy,
    DistributionStatus Status,
    bool IsOutstanding,
    int PrintCount,
    int? PrintLimit,
    string ScanCode,
    DateTimeOffset? AcknowledgedAt,
    DateTimeOffset? ReturnedAt,
    string? ClosureNote,
    DateTimeOffset CreatedAt)
{
    public static DistributionView From(DocumentDistribution copy, string scanCode) => new(
        copy.Id,
        copy.DocumentId,
        copy.CopyNumber,
        copy.CopyType,
        copy.IssuedToName,
        copy.IssuedBy,
        copy.Status,
        copy.IsOutstanding,
        copy.PrintCount,
        copy.PrintLimit,
        scanCode,
        copy.AcknowledgedAt,
        copy.ReturnedAt,
        copy.ClosureNote,
        copy.CreatedAt);
}

/// <param name="Password">
/// The performer's signing credential. Required because CloseOutCopy is one of the two actions
/// whose signature requirement cannot be configured away — writing off a controlled copy as
/// lost is a finding, and must be attributable to more than a logged-in session.
/// </param>
public sealed record CloseOutRequest(DistributionStatus Outcome, string Note, string? Password = null);

/// <summary>A row in the retrieval worklist: a copy still out for a document no longer current.</summary>
public sealed record PendingRetrievalView(
    Guid DistributionId,
    Guid DocumentId,
    string DocumentNumber,
    int Revision,
    string Title,
    DocumentStatus DocumentStatus,
    int CopyNumber,
    CopyType CopyType,
    string IssuedToName,
    DistributionStatus CopyStatus,
    string ScanCode,
    DateTimeOffset IssuedAt);

public sealed record PrintEventView(
    Guid Id,
    Guid DistributionId,
    int CopyNumber,
    int PrintSequence,
    string PrintedBy,
    string Watermark,
    DateTimeOffset PrintedAt);
