using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dms.Infrastructure.Storage;

/// <summary>
/// Mints the HS256 JSON Web Tokens the OnlyOffice Document Server validates requests against.
/// <para>
/// Hand-rolled rather than pulled from a library, and worth justifying: this needs to produce
/// one specific token shape for one specific consumer, the algorithm is fixed at HS256, and
/// there is no verification side — DMS signs, OnlyOffice verifies. Adding a JWT package to
/// Dms.Infrastructure for ~20 lines of well-understood work would widen the dependency surface
/// of a project that otherwise keeps it deliberately narrow.
/// </para>
/// <para>
/// Distinct from <c>HmacEditorTokenService</c>, which signs DMS's own public file URLs in a
/// custom <c>id.expiry.signature</c> format. Same secret, different scheme; the document
/// server accepts only a real JWT, and DMS's own endpoints accept only the custom format.
/// Neither will validate the other's tokens, which is why both exist.
/// </para>
/// </summary>
public static class OnlyOfficeJwt
{
    public static string Sign(IReadOnlyDictionary<string, object?> payload, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(
                "DocumentServer:TokenSecret is not set, so conversion requests cannot be signed. "
                + "It must match the document server's own JWT_SECRET.");
        }

        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(
            new Dictionary<string, string> { ["alg"] = "HS256", ["typ"] = "JWT" }));

        var body = Encode(JsonSerializer.SerializeToUtf8Bytes(payload));

        var signingInput = $"{header}.{body}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = Encode(hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput)));

        return $"{signingInput}.{signature}";
    }

    /// <summary>
    /// base64url: standard Base64 with the URL-unsafe characters swapped and padding removed.
    /// A plain Base64 string would be rejected — JWT is specified on base64url, and '+' or '/'
    /// in a token is a silent validation failure rather than an obvious one.
    /// </summary>
    private static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
