using Dms.Domain.Entities;

namespace Dms.Application.Abstractions;

/// <summary>
/// Mints an access token for an authenticated user. Implemented at the API edge, where the
/// signing keys live — the Application layer should not know or care that the token happens to
/// be a JWT.
/// </summary>
public interface IAccessTokenIssuer
{
    string Issue(DmsUser user, DateTimeOffset expiresAt);
}

/// <summary>Login policy thresholds, configured per deployment.</summary>
public interface IAuthPolicy
{
    /// <summary>Failed logins before the account locks.</summary>
    int FailedLoginThreshold { get; }

    TimeSpan LockoutDuration { get; }

    /// <summary>
    /// How long an access token stays valid. Short by default: there is no refresh token yet,
    /// so this is also how often a user is asked to log in again — a real trade-off between
    /// exposure of a stolen token and interrupting someone mid-task.
    /// </summary>
    TimeSpan TokenLifetime { get; }
}
