using System.Security.Cryptography;

namespace Dms.Domain.Services;

/// <summary>
/// Hashes document content for §11.70 record-binding: every applied signature carries the hash
/// of the exact bytes it was applied to, so a signature can never be quietly reattributed to a
/// document that has since changed.
/// <para>
/// Lower-case hex rather than Base64, so the hash reads identically wherever it's displayed —
/// truncated in an audit entry, compared in a support ticket, pasted into a query — without a
/// case-sensitivity trap.
/// </para>
/// <para>
/// <b>Reconstructed file.</b> Present in the working codebase before this review but absent
/// from the uploaded archive. SHA-256 over the raw bytes is the only behaviour every calling
/// site depends on; nothing else about this file's original shape could be inferred.
/// </para>
/// </summary>
public static class ContentHasher
{
    public static string Hash(byte[] content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));
}
