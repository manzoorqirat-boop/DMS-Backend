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
    string Designation);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
