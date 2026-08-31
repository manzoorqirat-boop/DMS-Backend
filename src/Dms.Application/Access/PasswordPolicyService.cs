using Dms.Application.Abstractions;
using Dms.Application.Common;
using Dms.Domain.Enums;

namespace Dms.Application.Access;

/// <summary>
/// Reads and updates the organisation's password policy.
/// <para>
/// Gated on <see cref="Permission.UserManage"/> for writes — the same permission that governs
/// creating accounts, because loosening the password policy and issuing a weak account are the
/// same act with different steps. Reads are open to any authenticated caller so the change-
/// password screen can tell someone the rules before they type, rather than after.
/// </para>
/// </summary>
public sealed class PasswordPolicyService(
    IPasswordPolicyRepository policies,
    IAccessControl access,
    IAuditTrail audit,
    ICurrentUser currentUser)
{
    private const string EntityType = "PasswordPolicy";

    public async Task<Result<PasswordPolicyView>> GetAsync(CancellationToken cancellationToken)
    {
        var policy = await policies.GetAsync(cancellationToken);
        return PasswordPolicyView.From(policy);
    }

    public async Task<Result<PasswordPolicyView>> UpdateAsync(
        UpdatePasswordPolicyRequest request,
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
                $"{Permission.UserManage} at organisation-wide scope is required to change the password policy.");
        }

        var policy = await policies.GetAsync(cancellationToken);

        // Captured before the update so the audit entry can show what actually moved — "the
        // policy was changed" is far less useful to an inspector than "expiry went from 90
        // days to 365".
        var before =
            $"min {policy.MinimumLength}, expiry {policy.ExpiryDays}d, history {policy.HistoryCount}, " +
            $"lockout {policy.MaxFailedAttempts}/{policy.LockoutMinutes}m, complexity {policy.RequireComplexity}";

        policy.Update(
            request.MinimumLength,
            request.ExpiryDays,
            request.HistoryCount,
            request.MaxFailedAttempts,
            request.LockoutMinutes,
            request.RequireComplexity,
            actor);

        var after =
            $"min {policy.MinimumLength}, expiry {policy.ExpiryDays}d, history {policy.HistoryCount}, " +
            $"lockout {policy.MaxFailedAttempts}/{policy.LockoutMinutes}m, complexity {policy.RequireComplexity}";

        audit.Record(
            AuditAction.PasswordPolicyChanged, EntityType, policy.Id, "Password policy",
            $"Changed from [{before}] to [{after}].");

        var outcome = await policies.SaveChangesAsync(cancellationToken);

        return outcome.Saved
            ? PasswordPolicyView.From(policy)
            : Error.Conflict("policy_save_conflict", "The password policy could not be saved.");
    }
}
