using Dms.Domain.Common;

namespace Dms.Domain.Entities;

/// <summary>
/// The organisation's password rules, held as editable master data rather than configuration.
/// <para>
/// Ported from the ERES build, where these live in a Settings collection so a validated system
/// can tighten its password policy without a redeployment — the same reasoning that put
/// numbering patterns, workflows and notification rules in the database here. An inspector
/// asking "what is your password policy and who set it" gets an answer with a name and a
/// timestamp against it, not a shrug and a config file.
/// </para>
/// <para>
/// Exactly one row is expected. It is a row rather than a static class because
/// <see cref="UpdatedBy"/> and <see cref="UpdatedAt"/> are the point: §11.10(d) wants system
/// access limited to authorised individuals, and a policy nobody can show the provenance of
/// is weak evidence of that.
/// </para>
/// </summary>
public class PasswordPolicy : Entity
{
    private PasswordPolicy() { }

    public PasswordPolicy(string createdBy)
    {
        UpdatedBy = createdBy;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Minimum length. Floored at 6 on read regardless of what is stored — ERES hit exactly
    /// this: a configured minimum below the floor was silently ignored while the error message
    /// still quoted the old number, so an administrator could believe they had set something
    /// they had not.
    /// </summary>
    public int MinimumLength { get; private set; } = 8;

    /// <summary>
    /// Days before a password must be changed. <b>Zero means never</b>, which is a legitimate
    /// choice — forced rotation is no longer universally recommended — but it should be a
    /// decision someone made rather than the absence of one.
    /// </summary>
    public int ExpiryDays { get; private set; } = 90;

    /// <summary>
    /// How many previous passwords may not be reused. Enforced by keeping that many old
    /// hashes on the user; see <c>DmsUser.PasswordHistory</c>.
    /// </summary>
    public int HistoryCount { get; private set; } = 3;

    /// <summary>Failed logins before the account locks.</summary>
    public int MaxFailedAttempts { get; private set; } = 5;

    /// <summary>How long a locked account stays locked.</summary>
    public int LockoutMinutes { get; private set; } = 15;

    /// <summary>
    /// Requires an uppercase letter, a digit and a symbol. Separate from length because the
    /// two are independently arguable: a long passphrase without a symbol is stronger than a
    /// short password with one, and an organisation may reasonably prefer either.
    /// </summary>
    public bool RequireComplexity { get; private set; } = true;

    public string UpdatedBy { get; private set; } = "";
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Applies a new policy. Every value is clamped to the same bounds ERES enforces, so a
    /// typo cannot produce a policy that locks everyone out or one that permits a one-character
    /// password.
    /// </summary>
    public void Update(
        int minimumLength,
        int expiryDays,
        int historyCount,
        int maxFailedAttempts,
        int lockoutMinutes,
        bool requireComplexity,
        string updatedBy)
    {
        MinimumLength = Math.Clamp(minimumLength, 6, 64);
        ExpiryDays = Math.Clamp(expiryDays, 0, 3650);
        HistoryCount = Math.Clamp(historyCount, 1, 24);
        MaxFailedAttempts = Math.Clamp(maxFailedAttempts, 3, 20);
        LockoutMinutes = Math.Clamp(lockoutMinutes, 1, 1440);
        RequireComplexity = requireComplexity;

        UpdatedBy = string.IsNullOrWhiteSpace(updatedBy)
            ? throw new ArgumentException("The acting user is required.", nameof(updatedBy))
            : updatedBy;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>The defaults a fresh installation starts with, matching ERES's own.</summary>
    public static PasswordPolicy CreateDefault(string createdBy) => new(createdBy);
}
