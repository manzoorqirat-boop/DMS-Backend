using Dms.Domain.Common;
using Dms.Domain.Services;

namespace Dms.Domain.Entities;

/// <summary>
/// A person who can author, review or approve controlled documents.
/// <para>
/// This entity exists because signing moved in-house. 21 CFR Part 11 §11.200(a)(1) requires an
/// electronic signature to use at least two distinct identification components, and §11.10(d)
/// requires system access be limited to authorised individuals — neither is satisfiable
/// without somewhere to check a credential against.
/// </para>
/// <para>
/// <see cref="FullName"/>, <see cref="Department"/> and <see cref="Designation"/> are held here
/// and copied onto each signature at the moment of signing. §11.50(a)(1) requires the printed
/// name of the signer to appear with the signature, and it must read as it did <i>then</i> —
/// resolving it live would silently rewrite three-year-old approvals when somebody changes
/// department.
/// </para>
/// </summary>
public class DmsUser : Entity, ITimestamped
{
    private DmsUser() { }

    public DmsUser(
        string userName,
        string fullName,
        string department,
        string designation,
        string password)
    {
        UserName = string.IsNullOrWhiteSpace(userName)
            ? throw new ArgumentException("Username is required.", nameof(userName))
            : userName.Trim().ToLowerInvariant();
        FullName = Require(fullName, nameof(fullName));
        Department = Require(department, nameof(department));
        Designation = Require(designation, nameof(designation));
        PasswordHash = PasswordHasher.Hash(password);
        CreatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Lowercased at construction so lookups are case-insensitive without relying on collation.</summary>
    public string UserName { get; private set; } = "";

    public string FullName { get; private set; } = "";
    public string Department { get; private set; } = "";
    public string Designation { get; private set; } = "";

    /// <summary>
    /// Optional. Nullable rather than required because an account is perfectly usable without
    /// one — plenty of shop-floor users sign documents and never receive mail — and making it
    /// mandatory would force placeholder addresses into a regulated user record.
    /// Notifications for a user with no address stay in-app rather than failing.
    /// </summary>
    public string? Email { get; private set; }

    public string PasswordHash { get; private set; } = "";

    public bool IsActive { get; private set; } = true;

    public int FailedSigningAttempts { get; private set; }

    /// <summary>
    /// Set when consecutive failed signing attempts cross the threshold. §11.300(d) expects
    /// use of an unauthorised credential to be detected and reported, which means the account
    /// has to actually stop working rather than simply logging the attempts.
    /// </summary>
    public DateTimeOffset? LockedOutUntil { get; private set; }

    /// <summary>
    /// Failed login attempts, counted separately from signing attempts.
    /// <para>
    /// Separate on purpose. Part 11 §11.200 treats the e-signature credential as distinct from
    /// the general login, and a shared counter would make the audit trail ambiguous about which
    /// credential was actually being attacked — five failures could mean two login attempts and
    /// three signing attempts, which are very different events.
    /// </para>
    /// </summary>
    public int FailedLoginAttempts { get; private set; }

    public DateTimeOffset? LoginLockedUntil { get; private set; }

    public DateTimeOffset? LastLoginAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    public bool IsLockedOut(DateTimeOffset now) => LockedOutUntil is { } until && until > now;

    public bool CanSign(DateTimeOffset now) => IsActive && !IsLockedOut(now);

    public bool IsLoginLockedOut(DateTimeOffset now) =>
        LoginLockedUntil is { } until && until > now;

    public bool CanLogIn(DateTimeOffset now) => IsActive && !IsLoginLockedOut(now);

    /// <summary>
    /// Records a failed login and locks the account once the threshold is reached.
    /// <para>
    /// Locking login does not lock signing, and vice versa. Someone brute-forcing a password
    /// from outside shouldn't be able to stop a signer already at their desk from completing an
    /// approval — that turns an attempted intrusion into a denial of service against a
    /// regulated process.
    /// </para>
    /// </summary>
    public void RegisterFailedLogin(int threshold, TimeSpan lockoutDuration, DateTimeOffset now)
    {
        FailedLoginAttempts++;

        if (FailedLoginAttempts >= threshold)
        {
            LoginLockedUntil = now.Add(lockoutDuration);
        }
    }

    public void RegisterSuccessfulLogin(DateTimeOffset now)
    {
        FailedLoginAttempts = 0;
        LoginLockedUntil = null;
        LastLoginAt = now;
    }

    public bool VerifyPassword(string password) => PasswordHasher.Verify(password, PasswordHash);

    public void SetEmail(string? email) =>
        Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();

    public void ChangePassword(string newPassword)
    {
        PasswordHash = PasswordHasher.Hash(newPassword);
        FailedSigningAttempts = 0;
        LockedOutUntil = null;
        Touch();
    }

    /// <summary>
    /// Records a failed signing authentication and locks the account once the threshold is
    /// crossed.
    /// </summary>
    public void RegisterFailedSigningAttempt(int threshold, TimeSpan lockoutDuration, DateTimeOffset now)
    {
        FailedSigningAttempts++;

        if (FailedSigningAttempts >= threshold)
        {
            LockedOutUntil = now.Add(lockoutDuration);
        }

        Touch();
    }

    /// <summary>Clears the failure counter after a successful signing.</summary>
    public void RegisterSuccessfulSigning()
    {
        FailedSigningAttempts = 0;
        LockedOutUntil = null;
        Touch();
    }

    public void Deactivate()
    {
        IsActive = false;
        Touch();
    }

    public void Reactivate()
    {
        IsActive = true;
        FailedSigningAttempts = 0;
        LockedOutUntil = null;
        Touch();
    }

    private void Touch() => UpdatedAt = DateTimeOffset.UtcNow;

    private static string Require(string value, string paramName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{paramName} is required.", paramName)
            : value.Trim();
}
