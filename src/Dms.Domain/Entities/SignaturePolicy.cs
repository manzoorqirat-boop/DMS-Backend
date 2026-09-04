using System.Text.Json;
using Dms.Domain.Common;
using Dms.Domain.Enums;

namespace Dms.Domain.Entities;

/// <summary>
/// What a single controlled action requires before it counts as done.
/// </summary>
/// <param name="Action">Which action. Fixed by the enum, never editable.</param>
/// <param name="RequiresSignature">
/// The performer re-enters their password. Distinct from being logged in: §11.200 treats the
/// signing credential as something applied deliberately per act, not something a session
/// carries around. An unattended workstation must not be able to issue a controlled copy.
/// </param>
/// <param name="RequiresSecondSignature">A different person must also sign.</param>
/// <param name="Timing">Whether that second signature authorises the act or verifies it after.</param>
/// <param name="SecondSignerPermission">
/// What the countersigner must hold. Null means any authenticated user with access to the
/// document — rarely what a site wants, but it is theirs to decide.
/// </param>
public sealed record SignaturePoint(
    ControlledAction Action,
    bool RequiresSignature,
    bool RequiresSecondSignature,
    SecondSignatureTiming Timing,
    Permission? SecondSignerPermission);

/// <summary>
/// Which actions require a signature, and which require two.
/// <para>
/// Configurable master data rather than code, for the same reason numbering patterns and
/// notification rules are: whether issuing a controlled copy needs countersigning is a decision
/// a company's own quality system makes, and it varies between sites of the same company.
/// </para>
/// <para>
/// One row holding all points as JSON, like <see cref="DocumentStatusStamps"/>. The set is
/// fixed by <see cref="ControlledAction"/> and can never grow at runtime, so a table per action
/// would cost a migration and buy nothing.
/// </para>
/// </summary>
public class SignaturePolicy : Entity
{
    private SignaturePolicy() { }

    private SignaturePolicy(string createdBy)
    {
        PointsJson = JsonSerializer.Serialize(Defaults());
        UpdatedBy = createdBy;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public string PointsJson { get; private set; } = "";
    public string UpdatedBy { get; private set; } = "";
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// The configured points, with anything missing from storage filled from the defaults — so
    /// an action added to the enum later is never silently unprotected.
    /// </summary>
    public IReadOnlyList<SignaturePoint> Points
    {
        get
        {
            var stored = string.IsNullOrWhiteSpace(PointsJson)
                ? []
                : JsonSerializer.Deserialize<List<SignaturePoint>>(PointsJson) ?? [];

            var byAction = stored.ToDictionary(p => p.Action);

            return Defaults()
                .Select(d => byAction.TryGetValue(d.Action, out var configured) ? configured : d)
                .ToList();
        }
    }

    public SignaturePoint For(ControlledAction action) =>
        Points.First(p => p.Action == action);

    /// <summary>
    /// Actions whose signature requirement cannot be switched off.
    /// <para>
    /// Both are irreversible and both destroy or write off a controlled artefact. An
    /// administrator who removed the signature from record disposition would leave the
    /// destruction of retained records attributable to nothing more than a logged-in session —
    /// which is precisely the situation §11.10(e) exists to prevent. Everything else is the
    /// site's own call.
    /// </para>
    /// </summary>
    public static IReadOnlySet<ControlledAction> AlwaysRequireSignature { get; } =
        new HashSet<ControlledAction>
        {
            ControlledAction.RecordDisposition,
            ControlledAction.CloseOutCopy,
        };

    public void Update(IReadOnlyList<SignaturePoint> points, string updatedBy)
    {
        ArgumentNullException.ThrowIfNull(points);

        foreach (var action in AlwaysRequireSignature)
        {
            var point = points.FirstOrDefault(p => p.Action == action);

            if (point is null || !point.RequiresSignature)
            {
                throw new ArgumentException(
                    $"{action} must always require a signature — it destroys or writes off a "
                    + "controlled record, and cannot be attributable to a session alone.",
                    nameof(points));
            }
        }

        PointsJson = JsonSerializer.Serialize(points);
        UpdatedBy = string.IsNullOrWhiteSpace(updatedBy)
            ? throw new ArgumentException("The acting user is required.", nameof(updatedBy))
            : updatedBy;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static SignaturePolicy CreateDefault(string createdBy) => new(createdBy);

    /// <summary>
    /// Seeded defaults: sign the acts that change what is in circulation, countersign the two
    /// that cannot be undone.
    /// <para>
    /// Disposition uses AuthorisationBefore because a record destroyed before approval cannot be
    /// restored when approval is refused. Close-out uses VerificationAfter because the copy is
    /// already gone by the time anyone records it — the second signature confirms the write-off
    /// was legitimate, which is all it can do at that point.
    /// </para>
    /// <para>
    /// Printing and retrieval start unsigned. Both are high-frequency and low-consequence, and a
    /// signature demanded dozens of times a day stops being a considered act — which is worse
    /// for compliance than not requiring one.
    /// </para>
    /// </summary>
    public static IReadOnlyList<SignaturePoint> Defaults() =>
    [
        new(ControlledAction.IssueCopy, true, false,
            SecondSignatureTiming.VerificationAfter, null),

        new(ControlledAction.RetrieveCopy, false, false,
            SecondSignatureTiming.VerificationAfter, null),

        new(ControlledAction.CloseOutCopy, true, true,
            SecondSignatureTiming.VerificationAfter, Permission.DocumentIssue),

        new(ControlledAction.PrintCopy, false, false,
            SecondSignatureTiming.VerificationAfter, null),

        new(ControlledAction.PeriodicReview, true, false,
            SecondSignatureTiming.VerificationAfter, null),

        new(ControlledAction.MakeObsolete, true, true,
            SecondSignatureTiming.AuthorisationBefore, Permission.DocumentObsolete),

        new(ControlledAction.RecordDisposition, true, true,
            SecondSignatureTiming.AuthorisationBefore, Permission.DocumentObsolete),
    ];
}
