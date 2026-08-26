using System.Security.Cryptography;

namespace Dms.Domain.Common;

/// <summary>
/// Generates RFC 9562 UUID version 7 identifiers.
/// <para>
/// .NET 9 added <c>Guid.CreateVersion7()</c> as a first-class BCL method. This project
/// deliberately targets .NET 8 — the LTS release, and the stack the original project plan
/// specified — where that method doesn't exist. This is the .NET 8-compatible equivalent,
/// built on the big-endian-aware <c>Guid(ReadOnlySpan&lt;byte&gt;, bool)</c> constructor that
/// .NET 8 itself introduced ahead of .NET 9's convenience wrapper, specifically so RFC-ordered
/// UUIDs like this one could be constructed correctly without the byte-scrambling the
/// ordinary little-endian <c>Guid</c> constructor applies to the first three fields.
/// </para>
/// <para>
/// The reason this codebase uses UUIDv7 instead of UUIDv4 everywhere — see every entity's own
/// remarks on ids "assigned at construction" — is that these sort close to insertion order,
/// keeping clustered-index fragmentation down on every primary key in the schema. Getting the
/// byte layout wrong wouldn't produce invalid or colliding GUIDs, only ones that silently
/// stopped being time-ordered — a far harder class of bug to notice than a compile error, and
/// exactly the kind of thing that deserves a test against RFC 9562 Appendix A.6's reference
/// vectors once a real .NET 8 toolchain is available to run one, rather than trusting this
/// comment.
/// </para>
/// </summary>
public static class Uuid7
{
    public static Guid NewGuid()
    {
        var bytes = new byte[16];

        // 48-bit Unix timestamp in milliseconds, big-endian, filling bytes 0–5.
        var unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bytes[0] = (byte)(unixMs >> 40);
        bytes[1] = (byte)(unixMs >> 32);
        bytes[2] = (byte)(unixMs >> 24);
        bytes[3] = (byte)(unixMs >> 16);
        bytes[4] = (byte)(unixMs >> 8);
        bytes[5] = (byte)unixMs;

        // 10 random bytes cover the 74 bits of randomness the format calls for (12-bit rand_a
        // + 62-bit rand_b); the version and variant fields below claim a few bits out of two
        // of them, which is expected and not a weakening of the randomness the spec asks for.
        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);

        // Byte 6: top nibble is the version (0111 = 7); bottom nibble is the top 4 bits of rand_a.
        bytes[6] = (byte)(0x70 | (random[0] & 0x0F));
        // Byte 7: the remaining 8 bits of rand_a.
        bytes[7] = random[1];
        // Byte 8: top 2 bits are the variant (10); bottom 6 bits are the top 6 bits of rand_b.
        bytes[8] = (byte)(0x80 | (random[2] & 0x3F));
        // Bytes 9–15: the remaining 56 bits of rand_b.
        random[3..10].CopyTo(bytes.AsSpan(9, 7));

        return new Guid(bytes, bigEndian: true);
    }
}
