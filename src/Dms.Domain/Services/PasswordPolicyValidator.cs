using Dms.Domain.Entities;

namespace Dms.Domain.Services;

/// <summary>
/// Checks a proposed password against the organisation's policy.
/// <para>
/// Pure and I/O-free, like the other domain services: the caller supplies the policy and the
/// candidate, and gets back either null or the single most relevant complaint. Ported from
/// the ERES build's validator, including its ordering — length before complexity, so someone
/// typing a short password is told that rather than being sent hunting for a symbol they were
/// going to need anyway.
/// </para>
/// </summary>
public static class PasswordPolicyValidator
{
    /// <summary>
    /// bcrypt silently truncates beyond 72 bytes, and PBKDF2 gains nothing from unbounded
    /// input while an enormous one wastes CPU on every login. ERES caps at 128; same here.
    /// </summary>
    public const int MaximumLength = 128;

    /// <summary>
    /// Absolute floor, applied whatever the stored policy says. ERES found that honouring a
    /// configured minimum below this silently did nothing while the message still quoted the
    /// old value — an administrator could believe they had loosened a rule that never moved.
    /// Clamping on write (<see cref="PasswordPolicy.Update"/>) and flooring again here means
    /// the two can't disagree.
    /// </summary>
    public const int AbsoluteMinimumLength = 6;

    private const string Symbols = "!@#$%^&*()_+-=[]{};':\"\\|,.<>/?";

    /// <summary>
    /// Returns the first policy violation, or null when the password is acceptable.
    /// </summary>
    public static string? Validate(string? password, PasswordPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (string.IsNullOrWhiteSpace(password))
        {
            return "A password is required.";
        }

        if (password.Length > MaximumLength)
        {
            return $"Password must be {MaximumLength} characters or fewer.";
        }

        var minimum = Math.Max(AbsoluteMinimumLength, policy.MinimumLength);
        if (password.Length < minimum)
        {
            return $"Password must be at least {minimum} characters.";
        }

        if (!policy.RequireComplexity)
        {
            return null;
        }

        if (!password.Any(char.IsUpper))
        {
            return "Password must contain at least one uppercase letter.";
        }

        if (!password.Any(char.IsDigit))
        {
            return "Password must contain at least one number.";
        }

        if (!password.Any(c => Symbols.Contains(c, StringComparison.Ordinal)))
        {
            return "Password must contain at least one special character.";
        }

        return null;
    }

    /// <summary>
    /// Whether a password has passed its expiry date under the current policy.
    /// <para>
    /// An <see cref="PasswordPolicy.ExpiryDays"/> of zero means passwords never expire, and is
    /// answered false here rather than treated as "expires immediately" — the difference
    /// between a deliberate choice and locking out every user at once.
    /// </para>
    /// </summary>
    public static bool HasExpired(DateTimeOffset passwordLastChanged, PasswordPolicy policy, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return policy.ExpiryDays > 0
               && (asOf - passwordLastChanged).TotalDays >= policy.ExpiryDays;
    }
}
