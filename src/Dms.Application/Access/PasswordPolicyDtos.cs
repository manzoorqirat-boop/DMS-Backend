using Dms.Domain.Entities;

namespace Dms.Application.Access;

public sealed record PasswordPolicyView(
    int MinimumLength,
    int ExpiryDays,
    int HistoryCount,
    int MaxFailedAttempts,
    int LockoutMinutes,
    bool RequireComplexity,
    string UpdatedBy,
    DateTimeOffset UpdatedAt)
{
    public static PasswordPolicyView From(PasswordPolicy policy) => new(
        policy.MinimumLength,
        policy.ExpiryDays,
        policy.HistoryCount,
        policy.MaxFailedAttempts,
        policy.LockoutMinutes,
        policy.RequireComplexity,
        policy.UpdatedBy,
        policy.UpdatedAt);
}

/// <summary>
/// Every value is clamped by <see cref="PasswordPolicy.Update"/> rather than rejected, so a
/// typo produces a sane policy instead of a 400 — but note that means what comes back may not
/// equal what was sent. The response carries the applied values for exactly that reason.
/// </summary>
public sealed record UpdatePasswordPolicyRequest(
    int MinimumLength,
    int ExpiryDays,
    int HistoryCount,
    int MaxFailedAttempts,
    int LockoutMinutes,
    bool RequireComplexity);
