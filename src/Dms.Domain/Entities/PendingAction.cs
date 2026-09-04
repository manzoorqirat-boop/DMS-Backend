using Dms.Domain.Common;
using Dms.Domain.Enums;

namespace Dms.Domain.Entities;

/// <summary>
/// A controlled action awaiting the second signature it requires.
/// <para>
/// This is what makes asynchronous countersigning work: the performer signs and leaves, and the
/// countersigner picks it up from a worklist minutes or days later. The alternative — both
/// people at one screen — needs no queue at all, which is why this entity only exists for the
/// asynchronous case.
/// </para>
/// <para>
/// <b>Whether the action has already taken effect depends on <see cref="Timing"/>.</b> Under
/// VerificationAfter the effect was applied when this row was created and the countersignature
/// confirms it was right; under AuthorisationBefore nothing has happened yet and completion is
/// what applies it. Reading a pending action without checking that field will give you the
/// wrong answer about the state of the world, which is why it is stored rather than looked up
/// from the policy: the policy can change while a row sits in the queue, and this row must
/// still mean what it meant when it was created.
/// </para>
/// <para>
/// <see cref="PayloadJson"/> carries the action's parameters so an AuthorisationBefore action
/// can be replayed on completion. It is deliberately opaque here — the domain has no business
/// knowing the shape of a close-out request — and is deserialised by whichever service owns
/// that action.
/// </para>
/// </summary>
public class PendingAction : Entity, ITimestamped
{
    private PendingAction() { }

    public PendingAction(
        ControlledAction action,
        SecondSignatureTiming timing,
        string subjectType,
        Guid subjectId,
        string subjectLabel,
        string payloadJson,
        Permission? countersignerPermission,
        Guid siteId,
        Guid departmentId)
    {
        Action = action;
        Timing = timing;
        SubjectType = RequireNonEmpty(subjectType, nameof(subjectType));
        SubjectId = subjectId;
        SubjectLabel = RequireNonEmpty(subjectLabel, nameof(subjectLabel));
        PayloadJson = payloadJson;
        CountersignerPermission = countersignerPermission;

        // Carried so the worklist can be scoped without joining back to the subject, which may
        // be a distribution, a document or something added later.
        SiteId = siteId;
        DepartmentId = departmentId;

        Status = PendingActionStatus.AwaitingCountersignature;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public ControlledAction Action { get; private set; }
    public SecondSignatureTiming Timing { get; private set; }

    /// <summary>What the action is about — "DocumentDistribution", "ControlledDocument".</summary>
    public string SubjectType { get; private set; } = "";

    public Guid SubjectId { get; private set; }

    /// <summary>
    /// Human-readable identity of the subject, captured at creation. Stored rather than resolved
    /// later so a worklist row still reads sensibly if the subject is since renumbered, and so
    /// the queue can be rendered without loading every subject.
    /// </summary>
    public string SubjectLabel { get; private set; } = "";

    public string PayloadJson { get; private set; } = "";

    public Permission? CountersignerPermission { get; private set; }

    public Guid SiteId { get; private set; }
    public Guid DepartmentId { get; private set; }

    public PendingActionStatus Status { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    /// <summary>Why the countersigner refused. Required on rejection.</summary>
    public string? RejectionReason { get; private set; }

    private readonly List<ActionSignature> _signatures = [];

    /// <summary>
    /// The signatures applied so far — the performer's first, then the countersigner's.
    /// </summary>
    public IReadOnlyList<ActionSignature> Signatures => _signatures;

    /// <summary>Who performed the action. Always the first signature.</summary>
    public ActionSignature? PerformedBy => _signatures.FirstOrDefault();

    public void AddSignature(ActionSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);

        if (Status != PendingActionStatus.AwaitingCountersignature)
        {
            throw new InvalidOperationException(
                $"This action is already {Status} and cannot take further signatures.");
        }

        // The whole point of a second signature. Letting one person supply both would make the
        // control theatre — and it is an easy mistake to make when someone holds every
        // permission, which is exactly when the check matters most.
        if (_signatures.Any(s => string.Equals(s.UserName, signature.UserName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"{signature.UserName} has already signed this action. A countersignature must "
                + "come from a different person.");
        }

        _signatures.Add(signature);
        Touch();
    }

    /// <summary>Marks the action fully signed. The caller applies the effect for AuthorisationBefore.</summary>
    public void Complete()
    {
        if (Status != PendingActionStatus.AwaitingCountersignature)
        {
            throw new InvalidOperationException($"This action is already {Status}.");
        }

        if (_signatures.Count < 2)
        {
            throw new InvalidOperationException(
                "An action awaiting countersignature needs two signatures before it can complete.");
        }

        Status = PendingActionStatus.Completed;
        ResolvedAt = DateTimeOffset.UtcNow;
        Touch();
    }

    /// <summary>
    /// The countersigner refused.
    /// <para>
    /// Under AuthorisationBefore nothing was applied, so refusal simply ends it. Under
    /// VerificationAfter the act already happened and cannot be undone by refusing to verify
    /// it — what a rejection produces there is a recorded discrepancy, which is the honest
    /// outcome and the one an investigation starts from.
    /// </para>
    /// </summary>
    public void Reject(string reason)
    {
        if (Status != PendingActionStatus.AwaitingCountersignature)
        {
            throw new InvalidOperationException($"This action is already {Status}.");
        }

        RejectionReason = string.IsNullOrWhiteSpace(reason)
            ? throw new ArgumentException("A rejection requires a stated reason.", nameof(reason))
            : reason.Trim();

        Status = PendingActionStatus.Rejected;
        ResolvedAt = DateTimeOffset.UtcNow;
        Touch();
    }

    /// <summary>
    /// Withdrawn by whoever requested it. Only meaningful before anyone countersigns, and only
    /// for AuthorisationBefore — cancelling a VerificationAfter action would claim to undo
    /// something that already happened.
    /// </summary>
    public void Cancel()
    {
        if (Status != PendingActionStatus.AwaitingCountersignature)
        {
            throw new InvalidOperationException($"This action is already {Status}.");
        }

        if (Timing == SecondSignatureTiming.VerificationAfter)
        {
            throw new InvalidOperationException(
                "This action has already taken effect and cannot be cancelled. If it was wrong, "
                + "reject the verification so the discrepancy is recorded.");
        }

        Status = PendingActionStatus.Cancelled;
        ResolvedAt = DateTimeOffset.UtcNow;
        Touch();
    }

    private void Touch() => UpdatedAt = DateTimeOffset.UtcNow;

    private static string RequireNonEmpty(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{name} is required.", name)
            : value.Trim();
}

/// <summary>
/// A signature applied to a controlled action.
/// <para>
/// Separate from <see cref="ElectronicSignature"/> rather than a generalisation of it, and that
/// is a deliberate choice. ElectronicSignature binds to a document's content hash — that
/// binding is what makes an approval mean something under §11.70, and it is the most
/// compliance-critical code in the system. Widening it to cover actions that have no content
/// hash would mean nullable columns on the path that must never be ambiguous, for the sake of
/// reusing four string fields.
/// </para>
/// <para>
/// Captures the same §11.50(a) manifest data: printed name, the capacity the person signed in,
/// the meaning, and the time. Department and designation are copied at signing rather than read
/// from the user later, so a subsequent job change cannot rewrite what a signature said.
/// </para>
/// </summary>
/// <remarks>
/// Immutable once written — no Touch, no mutators. A signature that could be edited after the
/// fact would not be a signature.
/// </remarks>
public class ActionSignature : Entity, ITimestamped
{
    private ActionSignature() { }

    public ActionSignature(
        Guid pendingActionId,
        Guid userId,
        string userName,
        string fullName,
        string department,
        string designation,
        ActionSignatureMeaning meaning,
        string? reason)
    {
        PendingActionId = pendingActionId;
        UserId = userId;
        UserName = RequireNonEmpty(userName, nameof(userName));
        FullName = RequireNonEmpty(fullName, nameof(fullName));
        Department = RequireNonEmpty(department, nameof(department));
        Designation = RequireNonEmpty(designation, nameof(designation));
        Meaning = meaning;

        Reason = meaning == ActionSignatureMeaning.Refused
            ? (string.IsNullOrWhiteSpace(reason)
                ? throw new ArgumentException("A refusal requires a stated reason.", nameof(reason))
                : reason.Trim())
            : (string.IsNullOrWhiteSpace(reason) ? null : reason.Trim());

        SignedAt = DateTimeOffset.UtcNow;
        CreatedAt = SignedAt;
    }

    public Guid PendingActionId { get; private set; }
    public Guid UserId { get; private set; }
    public string UserName { get; private set; } = "";
    public string FullName { get; private set; } = "";
    public string Department { get; private set; } = "";
    public string Designation { get; private set; } = "";
    public ActionSignatureMeaning Meaning { get; private set; }
    public string? Reason { get; private set; }
    public DateTimeOffset SignedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt => null;

    private static string RequireNonEmpty(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{name} is required.", name)
            : value.Trim();
}
