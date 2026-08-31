using Dms.Application.Abstractions;
using Dms.Application.Common;
using Dms.Domain.Enums;
using Dms.Domain.Services;

namespace Dms.Application.Auth;

/// <summary>
/// Authenticates a user and issues an access token.
/// <para>
/// This is the <b>general login</b> credential only. It deliberately does not grant the
/// ability to sign: applying an electronic signature re-authenticates with the password every
/// time, which is what 21 CFR Part 11 §11.200 requires and what
/// <c>ReviewWorkflowService</c> already enforces. A token here is proof of identity, not proof
/// of intent to sign.
/// </para>
/// </summary>
public sealed class AuthService(
    IUserRepository users,
    IPasswordPolicyRepository passwordPolicies,
    IAccessTokenIssuer tokens,
    IAuthPolicy policy,
    IAuditTrail audit,
    IClock clock)
{
    private const string EntityType = "DmsUser";

    public async Task<Result<LoginResult>> LoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password))
        {
            return Error.Validation("credentials_required", "Username and password are required.");
        }

        var user = await users.GetByUserNameAsync(request.UserName.Trim(), cancellationToken);

        // Same message and same code for an unknown user as for a wrong password. Telling a
        // caller which usernames exist turns a password guess into a two-step problem.
        if (user is null)
        {
            return InvalidCredentials();
        }

        var now = clock.UtcNow;

        if (!user.IsActive)
        {
            // Also reported as invalid credentials rather than "account disabled", for the same
            // reason — but recorded accurately in the audit trail, where the distinction
            // matters and the reader is already authorised.
            audit.Record(
                AuditAction.UserLoginFailed, EntityType, user.Id, user.UserName,
                "Login attempted against a deactivated account.", actor: user.UserName);
            await users.SaveChangesAsync(cancellationToken);
            return InvalidCredentials();
        }

        if (user.IsLoginLockedOut(now))
        {
            audit.Record(
                AuditAction.UserLoginFailed, EntityType, user.Id, user.UserName,
                $"Login attempted while locked out until {user.LoginLockedUntil:yyyy-MM-dd HH:mm} UTC.",
                actor: user.UserName);
            await users.SaveChangesAsync(cancellationToken);

            // Lockout *is* disclosed, unlike the cases above. A legitimate user who has locked
            // themselves out needs to know why waiting will help; and an attacker who triggered
            // it already knows.
            return Error.Validation(
                "account_locked",
                $"The account is locked until {user.LoginLockedUntil:yyyy-MM-dd HH:mm} UTC.");
        }

        if (!user.VerifyPassword(request.Password))
        {
            user.RegisterFailedLogin(policy.FailedLoginThreshold, policy.LockoutDuration, now);

            var lockedNow = user.IsLoginLockedOut(clock.UtcNow);

            audit.Record(
                lockedNow ? AuditAction.UserLoginLockedOut : AuditAction.UserLoginFailed,
                EntityType, user.Id, user.UserName,
                lockedNow
                    ? $"Locked out after {user.FailedLoginAttempts} failed login attempt(s)."
                    : $"Failed login attempt {user.FailedLoginAttempts} of {policy.FailedLoginThreshold}.",
                actor: user.UserName);

            await users.SaveChangesAsync(cancellationToken);
            return InvalidCredentials();
        }

        user.RegisterSuccessfulLogin(now);

        // Expiry is evaluated at login rather than by a sweep: a password that expired last
        // night matters at the moment someone tries to use it, and nowhere else. Setting the
        // flag on the entity (rather than only reporting it) means the requirement survives
        // the user closing the tab and coming back.
        var passwordPolicy = await passwordPolicies.GetAsync(cancellationToken);
        var expired = PasswordPolicyValidator.HasExpired(user.PasswordLastChanged, passwordPolicy, now);

        if (expired && !user.MustChangePassword)
        {
            user.RequirePasswordChange();

            audit.Record(
                AuditAction.UserPasswordChanged, EntityType, user.Id, user.UserName,
                $"Password expired after {passwordPolicy.ExpiryDays} days; change required before further use.",
                actor: user.UserName);
        }

        var mustChange = user.MustChangePassword;
        var changeReason = mustChange
            ? (expired ? "password_expired" : "new_account")
            : null;

        var expiresAt = now.Add(policy.TokenLifetime);
        var token = tokens.Issue(user, expiresAt);

        audit.Record(
            AuditAction.UserLoggedIn, EntityType, user.Id, user.UserName,
            $"Signed in. Token valid until {expiresAt:yyyy-MM-dd HH:mm} UTC.", actor: user.UserName);

        var saved = await users.SaveChangesAsync(cancellationToken);
        if (!saved.Saved)
        {
            return Error.Conflict("login_save_conflict", "The login could not be recorded.");
        }

        return Result<LoginResult>.Success(new LoginResult(
            token,
            expiresAt,
            user.UserName,
            user.FullName,
            user.Department,
            user.Designation,
            mustChange,
            changeReason));
    }

    private static Error InvalidCredentials() =>
        Error.Validation("invalid_credentials", "Username or password is incorrect.");
}
