using Dms.Domain.Entities;
using Dms.Domain.Enums;

namespace Dms.Application.Signing;

/// <summary>
/// A row in the countersignature worklist.
/// <para>
/// <see cref="HasTakenEffect"/> is the field that matters most on this screen. Under
/// VerificationAfter the act already happened and refusing only records a discrepancy; under
/// AuthorisationBefore nothing has happened and refusing simply ends it. A countersigner who
/// doesn't know which they are looking at cannot make an informed decision, so it is stated
/// rather than left to be inferred from the timing enum.
/// </para>
/// </summary>
public sealed record PendingActionView(
    Guid Id,
    ControlledAction Action,
    SecondSignatureTiming Timing,
    bool HasTakenEffect,
    string SubjectType,
    Guid SubjectId,
    string SubjectLabel,
    Permission? CountersignerPermission,
    string PerformedBy,
    string PerformedByFullName,
    DateTimeOffset PerformedAt,
    PendingActionStatus Status)
{
    public static PendingActionView From(PendingAction action)
    {
        var performer = action.PerformedBy;

        return new PendingActionView(
            action.Id,
            action.Action,
            action.Timing,
            action.Timing == SecondSignatureTiming.VerificationAfter,
            action.SubjectType,
            action.SubjectId,
            action.SubjectLabel,
            action.CountersignerPermission,
            performer?.UserName ?? "(unknown)",
            performer?.FullName ?? "(unknown)",
            performer?.SignedAt ?? action.CreatedAt,
            action.Status);
    }
}

/// <summary>
/// Body of POST /api/pending-actions/{id}/countersign.
/// </summary>
/// <param name="Password">
/// The countersigner's own signing credential. §11.200's second component, never stored and
/// never logged.
/// </param>
/// <param name="Approve">False refuses, which requires a reason.</param>
public sealed record CountersignRequest(string Password, bool Approve, string? Reason);

/// <summary>What a caller must supply to perform an action, given the current policy.</summary>
public sealed record SignaturePointView(
    ControlledAction Action,
    bool RequiresSignature,
    bool RequiresSecondSignature,
    SecondSignatureTiming Timing,
    Permission? SecondSignerPermission)
{
    public static SignaturePointView From(SignaturePoint point) => new(
        point.Action,
        point.RequiresSignature,
        point.RequiresSecondSignature,
        point.Timing,
        point.SecondSignerPermission);
}

public sealed record UpdateSignaturePolicyRequest(IReadOnlyList<SignaturePointView> Points);
