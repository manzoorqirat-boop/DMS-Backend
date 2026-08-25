using System.Security.Cryptography;

namespace Dms.Domain.Services;

/// <summary>
/// Hashes and verifies passwords with PBKDF2-HMAC-SHA256.
/// <para>
/// Pure and I/O-free, like the other domain services. The stored format is
/// <c>PBKDF2$iterations$saltBase64$hashBase64</c> — versioned by iteration count rather than a
/// bare hash, so a future increase to the work factor doesn't invalidate every password already
/// stored: <see cref="Verify"/> reads whatever count is embedded in the hash it's checking
/// against, not a compiled-in constant.
/// </para>
/// <para>
/// <b>Reconstructed file.</b> This entity was present in the working codebase before this
/// review but was absent from the uploaded archive. The iteration count and salt/hash sizing
/// below are reasonable current defaults, not a guaranteed match to whatever the original file
/// specified — if any password hashes already exist in a database created under a different
/// format, they will fail to verify against this implementation. Treat this as safe only for a
/// fresh database, and confirm the format against the original file if one can be recovered.
/// </para>
/// </summary>
public static class PasswordHasher
{
    private const string Prefix = "PBKDF2";
    private const int DefaultIterations = 210_000;
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;

    /// <summary>Hashes a password, embedding the work factor and a fresh random salt.</summary>
    public static string Hash(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("Password is required.", nameof(password));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, DefaultIterations, HashAlgorithmName.SHA256, HashSizeBytes);

        return $"{Prefix}${DefaultIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// Verifies a password against a stored hash. Returns false rather than throwing on a
    /// malformed or foreign hash — a corrupt stored value should read as "wrong password," not
    /// crash the login path.
    /// </summary>
    public static bool Verify(string password, string storedHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(storedHash))
        {
            return false;
        }

        var parts = storedHash.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix)
        {
            return false;
        }

        if (!int.TryParse(parts[1], out var iterations) || iterations <= 0)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        // Fixed-time comparison: a length- or byte-dependent short-circuit here leaks
        // information about the correct hash through response timing.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
