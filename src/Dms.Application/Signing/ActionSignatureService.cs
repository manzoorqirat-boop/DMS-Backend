using System.Text.Json;
using Dms.Application.Abstractions;
using Dms.Application.Common;
using Dms.Domain.Entities;
using Dms.Domain.Enums;

namespace Dms.Application.Signing;

/// <summary>
/// Enforces the configured signature points on controlled actions, and runs the queue of
/// actions awaiting a countersignature.
/// <para>
/// Every action that can require signing goes through <see cref="RequireAsync"/> before it is
/// performed. The result tells the caller one of three things: proceed, proceed but record that
/// a verification is now owed, or do nothing yet because authorisation must come first. Callers
/// that ignore the third case would defeat the control entirely, which is why the return type
/// makes it awkward to ignore.
/// </para>
/// </summary>
public sealed class ActionSignatureService(
    ISignaturePolicyRepository policies,
    IPendingActionRepository pendingActions,
    IUserRepository users,
    IAccessControl access,
    IAuditTrail audit,
    ICurrentUser currentUser,
    IClock clock)
{
    private const string EntityType = "PendingAction";

    /// <summary>What the caller should do, having satisfied the signature requirement.</summary>
    public enum Outcome
    {
        /// <summary>No signature was required, or one was and it was given. Perform the action.</summary>
        Proceed,

        /// <summary>
        /// Perform the action, and record that a second person still owes a verification. The
        /// returned pending action is that obligation.
        /// </summary>
        ProceedPendingVerification,

        /// <summary>
        /// Do <b>not</b> perform the action. It is queued for authorisation and will be applied
        /// when someone countersigns.
        /// </summary>
        Queued,
    }

    public sealed record Requirement(Outcome Outcome, PendingAction? Pending);

    /// <summary>
    /// Checks and records the signature a controlled action requires.
    /// </summary>
    /// <param name="password">
    /// The performer's signing credential, re-entered. Null is only acceptable when the action
    /// requires no signature — being logged in is not a signature, because an unattended
    /// workstation would then be able to issue controlled copies.
    /// </param>
    /// <param name="payload">
    /// Action parameters, serialised, so an authorisation-before action can be replayed when it
    /// is finally approved. Ignored for actions that take effect immediately.
    /// </param>
    public async Task<Result<Requirement>> RequireAsync(
        ControlledAction action,
        string subjectType,
        Guid subjectId,
        string subjectLabel,
        Guid siteId,
        Guid departmentId,
        string? password,
        object? payload,
        CancellationToken cancellationToken)
    {
        var policy = await policies.GetAsync(cancellationToken);
        var point = policy.For(action);

        if (!point.RequiresSignature)
        {
            return new Requirement(Outcome.Proceed, null);
        }

        if (currentUser.UserName is not { } actor || string.IsNullOrWhiteSpace(actor))
        {
            return Error.Validation("actor_unknown", "The acting user could not be determined.");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            return Error.Validation(
                "signature_required",
                $"{action} requires your electronic signature. Re-enter your password to confirm.");
        }

        var user = await users.GetByUserNameAsync(actor, cancellationToken);
        if (user is null)
        {
            return Error.Validation("actor_unknown", "The acting user could not be resolved.");
        }

        var now = clock.UtcNow;

        if (user.IsLockedOut(now))
        {
            return Error.Validation(
                "signing_locked_out",
                "Signing is locked for this account after repeated failed attempts.");
        }

        if (!user.VerifyPassword(password))
        {
            user.RegisterFailedSigningAttempt(now);
            await users.SaveChangesAsync(cancellationToken);

            audit.Record(
                AuditAction.SignatureAuthenticationFailed, EntityType, subjectId, subjectLabel,
                $"Failed signing attempt for {action} by {actor}.");

            return Error.Validation(
                "signature_invalid", "That password was not correct. The attempt was recorded.");
        }

        user.RegisterSuccessfulSigning();

        // No countersignature wanted: one signature is the whole requirement, and there is
        // nothing to queue.
        if (!point.RequiresSecondSignature)
        {
            audit.Record(
                AuditAction.SignatureApplied, EntityType, subjectId, subjectLabel,
                $"{action} signed by {user.FullName} ({actor}).");

            return new Requirement(Outcome.Proceed, null);
        }

        // One unresolved action per subject. Two concurrent close-outs of the same copy would
        // leave contradictory pending records, and whichever was countersigned second would
        // silently overwrite the first.
        if (await pendingActions.HasAwaitingAsync(subjectType, subjectId, cancellationToken))
        {
            return Error.Conflict(
                "action_already_pending",
                $"{subjectLabel} already has a {action} awaiting countersignature. Resolve that "
                + "one before starting another.");
        }

        var pending = new PendingAction(
            action,
            point.Timing,
            subjectType,
            subjectId,
            subjectLabel,
            payload is null ? "{}" : JsonSerializer.Serialize(payload),
            point.SecondSignerPermission,
            siteId,
            departmentId);

        pending.AddSignature(new ActionSignature(
            pending.Id, user.Id, user.UserName, user.FullName, user.Department, user.Designation,
            ActionSignatureMeaning.Performed, null));

        pendingActions.Add(pending);

        audit.Record(
            AuditAction.SignatureApplied, EntityType, pending.Id, subjectLabel,
            $"{action} signed by {user.FullName} ({actor}); awaiting countersignature"
            + (point.Timing == SecondSignatureTiming.AuthorisationBefore
                ? " before it takes effect."
                : " to verify it."));

        return new Requirement(
            point.Timing == SecondSignatureTiming.AuthorisationBefore
                ? Outcome.Queued
                : Outcome.ProceedPendingVerification,
            pending);
    }

    /// <summary>
    /// Applies a countersignature.
    /// <para>
    /// Returns the completed action so the caller can apply the effect for an
    /// authorisation-before case — this service deliberately does not know how to close out a
    /// copy or destroy a record, and giving it that knowledge would make it a dependency of
    /// everything.
    /// </para>
    /// </summary>
    public async Task<Result<PendingAction>> CountersignAsync(
        Guid pendingActionId,
        string password,
        bool approve,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserName is not { } actor || string.IsNullOrWhiteSpace(actor))
        {
            return Error.Validation("actor_unknown", "The acting user could not be determined.");
        }

        var pending = await pendingActions.GetAsync(pendingActionId, cancellationToken);
        if (pending is null)
        {
            return Error.NotFound("pending_action_not_found", "That action no longer exists.");
        }

        if (pending.Status != PendingActionStatus.AwaitingCountersignature)
        {
            return Error.Conflict(
                "action_already_resolved",
                $"That action is already {pending.Status} and cannot be countersigned.");
        }

        // Checked before the password, so someone without the permission is told that rather
        // than being invited to prove they know a credential that will not help them.
        if (pending.CountersignerPermission is { } required)
        {
            var permitted = await access.HasPermissionAsync(
                required, pending.SiteId, pending.DepartmentId, cancellationToken);

            if (!permitted)
            {
                return Error.Validation(
                    "permission_denied",
                    $"{required} is required to countersign this action.");
            }
        }

        var user = await users.GetByUserNameAsync(actor, cancellationToken);
        if (user is null)
        {
            return Error.Validation("actor_unknown", "The acting user could not be resolved.");
        }

        var now = clock.UtcNow;

        if (user.IsLockedOut(now))
        {
            return Error.Validation(
                "signing_locked_out",
                "Signing is locked for this account after repeated failed attempts.");
        }

        if (!user.VerifyPassword(password))
        {
            user.RegisterFailedSigningAttempt(now);
            await users.SaveChangesAsync(cancellationToken);

            audit.Record(
                AuditAction.SignatureAuthenticationFailed, EntityType, pending.Id, pending.SubjectLabel,
                $"Failed countersigning attempt by {actor}.");

            return Error.Validation(
                "signature_invalid", "That password was not correct. The attempt was recorded.");
        }

        user.RegisterSuccessfulSigning();

        var meaning = !approve
            ? ActionSignatureMeaning.Refused
            : pending.Timing == SecondSignatureTiming.AuthorisationBefore
                ? ActionSignatureMeaning.Authorised
                : ActionSignatureMeaning.Verified;

        try
        {
            // PendingAction refuses a second signature from the same person — the check that
            // makes a countersignature mean anything. Surfaced as a 409 rather than a 500
            // because it is a legitimate thing to attempt and a comprehensible thing to be told.
            pending.AddSignature(new ActionSignature(
                pending.Id, user.Id, user.UserName, user.FullName, user.Department,
                user.Designation, meaning, reason));

            if (approve)
            {
                pending.Complete();
            }
            else
            {
                pending.Reject(reason ?? "");
            }
        }
        catch (InvalidOperationException ex)
        {
            return Error.Conflict("countersignature_refused", ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Error.Validation("reason_required", ex.Message);
        }

        audit.Record(
            approve ? AuditAction.SignatureApplied : AuditAction.SignatureRejected,
            EntityType, pending.Id, pending.SubjectLabel,
            $"{pending.Action} {meaning.ToString().ToLowerInvariant()} by {user.FullName} ({actor})"
            + (reason is null ? "." : $". Reason: {reason}"));

        var outcome = await pendingActions.SaveChangesAsync(cancellationToken);

        return outcome.Saved
            ? Result<PendingAction>.Success(pending)
            : Error.Conflict("countersignature_save_conflict", "The countersignature could not be saved.");
    }

    /// <summary>The current signature points. Open to any authenticated caller.</summary>
    public async Task<Result<IReadOnlyList<SignaturePointView>>> GetPolicyAsync(
        CancellationToken cancellationToken)
    {
        var policy = await policies.GetAsync(cancellationToken);

        return Result<IReadOnlyList<SignaturePointView>>.Success(
            policy.Points.Select(SignaturePointView.From).ToList());
    }

    /// <summary>
    /// Changes which actions require signing.
    /// <para>
    /// Gated on <see cref="Permission.UserManage"/> at organisation scope — the same permission
    /// that governs creating accounts, because weakening a signature requirement and issuing an
    /// over-privileged account are the same act reached by different routes.
    /// </para>
    /// </summary>
    public async Task<Result<IReadOnlyList<SignaturePointView>>> UpdatePolicyAsync(
        UpdateSignaturePolicyRequest request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserName is not { } actor || string.IsNullOrWhiteSpace(actor))
        {
            return Error.Validation("actor_unknown", "The acting user could not be determined.");
        }

        var allowed = await access.HasPermissionAsync(
            Permission.UserManage, siteId: null, departmentId: null, cancellationToken);

        if (!allowed)
        {
            return Error.Validation(
                "permission_denied",
                $"{Permission.UserManage} at organisation-wide scope is required to change "
                + "signature requirements.");
        }

        var policy = await policies.GetAsync(cancellationToken);

        // Captured before the change so the audit entry can show what actually moved. "The
        // signature policy was changed" tells an inspector far less than "CloseOutCopy stopped
        // requiring a countersignature".
        var before = Describe(policy.Points);

        try
        {
            policy.Update(
                request.Points
                    .Select(p => new SignaturePoint(
                        p.Action, p.RequiresSignature, p.RequiresSecondSignature,
                        p.Timing, p.SecondSignerPermission))
                    .ToList(),
                actor);
        }
        catch (ArgumentException ex)
        {
            return Error.Validation("signature_floor", ex.Message);
        }

        audit.Record(
            AuditAction.SignaturePolicyChanged, EntityType, policy.Id, "Signature policy",
            $"Changed from [{before}] to [{Describe(policy.Points)}].");

        var outcome = await policies.SaveChangesAsync(cancellationToken);

        return outcome.Saved
            ? Result<IReadOnlyList<SignaturePointView>>.Success(
                policy.Points.Select(SignaturePointView.From).ToList())
            : Error.Conflict("policy_save_conflict", "The signature policy could not be saved.");
    }

    private static string Describe(IReadOnlyList<SignaturePoint> points) =>
        string.Join("; ", points.Select(p =>
            $"{p.Action}={(p.RequiresSignature ? "sign" : "none")}"
            + (p.RequiresSecondSignature ? $"+counter({p.Timing})" : "")));

    /// <summary>Everything still waiting on a countersignature, oldest first.</summary>
    public async Task<Result<IReadOnlyList<PendingAction>>> ListAwaitingAsync(
        CancellationToken cancellationToken) =>
        Result<IReadOnlyList<PendingAction>>.Success(
            await pendingActions.ListAwaitingAsync(cancellationToken));
}
