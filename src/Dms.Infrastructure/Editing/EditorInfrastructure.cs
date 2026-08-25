using System.Security.Cryptography;
using System.Text;
using Dms.Application.Abstractions;
using Microsoft.Extensions.Configuration;

namespace Dms.Infrastructure.Editing;

public static class EditorConfig
{
    public const string SectionName = "DocumentServer";

    public const string UrlKey = $"{SectionName}:Url";
    public const string CallbackBaseUrlKey = $"{SectionName}:CallbackBaseUrl";
    public const string TokenSecretKey = $"{SectionName}:TokenSecret";
    public const string SessionMinutesKey = $"{SectionName}:SessionMinutes";
}

public sealed class EditorSettings(IConfiguration configuration) : IEditorSettings
{
    public string DocumentServerUrl { get; } = configuration[EditorConfig.UrlKey] ?? "";

    public string CallbackBaseUrl { get; } = configuration[EditorConfig.CallbackBaseUrlKey] ?? "";

    public TimeSpan SessionLifetime { get; } =
        TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue(EditorConfig.SessionMinutesKey, 60), 5, 480));

    /// <summary>
    /// Both URLs are required. A document server URL with no callback URL would render an
    /// editor that can never save, which is worse than refusing to open one.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(DocumentServerUrl) && !string.IsNullOrWhiteSpace(CallbackBaseUrl);
}

/// <summary>
/// HMAC-signed, expiring tokens for the public editor routes.
/// <para>
/// Format is <c>sessionId.expiryUnix.signature</c>. The signature covers both the id and the
/// expiry, so neither can be edited without invalidating it — a token where only the id were
/// signed could be replayed forever by rewriting the expiry.
/// </para>
/// </summary>
public sealed class HmacEditorTokenService : IEditorTokenService
{
    private readonly byte[] _key;

    public HmacEditorTokenService(IConfiguration configuration)
    {
        var secret = configuration[EditorConfig.TokenSecretKey];

        // Fails at startup rather than minting guessable tokens. These URLs are the only thing
        // standing between an unauthenticated caller and a controlled document's contents.
        _key = string.IsNullOrWhiteSpace(secret) || secret.Length < 32
            ? throw new InvalidOperationException(
                $"{EditorConfig.TokenSecretKey} must be set to at least 32 characters when a "
                + "document server is configured.")
            : Encoding.UTF8.GetBytes(secret);
    }

    public string Issue(Guid sessionId, DateTimeOffset expiresAt)
    {
        var payload = $"{sessionId:N}.{expiresAt.ToUnixTimeSeconds()}";
        return $"{payload}.{Sign(payload)}";
    }

    public Guid? Validate(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        var payload = $"{parts[0]}.{parts[1]}";

        // Fixed-time comparison: plain string equality here leaks signature bytes through
        // timing, which is enough to forge one given patience.
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(Sign(payload)),
                Encoding.UTF8.GetBytes(parts[2])))
        {
            return null;
        }

        if (!long.TryParse(parts[1], out var expiryUnix)
            || DateTimeOffset.FromUnixTimeSeconds(expiryUnix) <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        return Guid.TryParseExact(parts[0], "N", out var sessionId) ? sessionId : null;
    }

    private string Sign(string payload)
    {
        using var hmac = new HMACSHA256(_key);
        return Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Fetches a saved document back from the document server over HTTP.</summary>
public sealed class HttpEditorContentFetcher(IHttpClientFactory factory) : IEditorContentFetcher
{
    public const string ClientName = "document-server";

    /// <summary>
    /// A failure returns null rather than throwing, so the caller can reject the save cleanly
    /// and leave the author's work on the document server rather than losing it to an
    /// unhandled exception in a callback handler.
    /// </summary>
    public async Task<byte[]?> FetchAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        try
        {
            using var client = factory.CreateClient(ClientName);
            using var response = await client.GetAsync(uri, cancellationToken);

            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsByteArrayAsync(cancellationToken)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }
}
