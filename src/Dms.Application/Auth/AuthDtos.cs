namespace Dms.Application.Auth;

public sealed record LoginRequest(string UserName, string Password);

/// <summary>
/// A successful login. Deliberately carries no permission list — a client rendering menus
/// calls <c>/api/roles/me/permissions</c> for that, scoped to the site and department it is
/// actually working in. Baking permissions into a token would freeze them until it expired,
/// so a revoked role would keep working.
/// </summary>
public sealed record LoginResult(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string UserName,
    string FullName,
    string Department,
    string Designation,
    /// <summary>
    /// True when the caller must change their password before doing anything else — either
    /// because the account was just created (the administrator who set the password knows it,
    /// and here the password is also the signing credential), or because it has passed the
    /// policy's expiry. The token IS issued either way, because changing a password requires
    /// being authenticated; the frontend is expected to route straight to the change screen
    /// and refuse to go elsewhere until this clears.
    /// </summary>
    bool MustChangePassword,
    /// <summary>Why, when MustChangePassword is set: "new_account" or "password_expired".</summary>
    string? PasswordChangeReason);
