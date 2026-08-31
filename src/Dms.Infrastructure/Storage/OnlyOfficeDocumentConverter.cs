using System.Net.Http.Json;
using System.Text.Json;
using Dms.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Dms.Infrastructure.Storage;

/// <summary>
/// Converts .docx to PDF using the OnlyOffice Document Server's conversion service.
/// <para>
/// Extracted from <c>OnlyOfficePrintRenderer</c>, which previously owned this logic outright.
/// Two callers now need it — controlled printing and the approved-PDF export — and two copies
/// of a staging-plus-polling protocol would have drifted apart, with the second copy's bugs
/// only surfacing on whichever path was exercised less.
/// </para>
/// <para>
/// The conversion service fetches its source by URL rather than accepting an upload, so the
/// file is briefly published at a token-guarded public endpoint for it to collect. That is why
/// <c>DocumentServer:CallbackBaseUrl</c> must be the API as the document server sees it, not as
/// a browser sees it.
/// </para>
/// </summary>
public sealed class OnlyOfficeDocumentConverter(
    IHttpClientFactory httpClientFactory,
    IEditorSettings settings,
    IEditorTokenService tokens,
    IDocumentFileStore files,
    ILogger<OnlyOfficeDocumentConverter> logger) : IDocumentConverter
{
    public const string ClientName = "onlyoffice-converter";

    /// <summary>
    /// Prefix for the short-lived staging copies the conversion service fetches. Distinct from
    /// real document keys so a stray staging file can never be mistaken for a working copy,
    /// and so it is obvious what to sweep if one is ever orphaned.
    /// </summary>
    public const string StagingPrefix = "convert-staging/";

    public bool IsAvailable => settings.IsConfigured;

    public async Task<byte[]> ToPdfAsync(byte[] docx, CancellationToken cancellationToken)
    {
        if (!settings.IsConfigured)
        {
            throw new InvalidOperationException(
                "No document server is configured, so documents cannot be converted to PDF. "
                + "Set DocumentServer:Url, CallbackBaseUrl and TokenSecret.");
        }

        var client = httpClientFactory.CreateClient(ClientName);
        var conversionUrl = $"{settings.DocumentServerUrl.TrimEnd('/')}/ConvertService.ashx";

        // A per-conversion id, used as both the staging key and the document server's cache
        // key. Reusing one would return a previous conversion's PDF.
        var conversionId = Guid.NewGuid();
        var stagingKey = $"{StagingPrefix}{conversionId:N}.docx";

        try
        {
            await files.SaveAsync(stagingKey, docx, cancellationToken);

            var token = tokens.Issue(conversionId, DateTimeOffset.UtcNow.AddMinutes(5));
            var sourceUrl =
                $"{settings.CallbackBaseUrl.TrimEnd('/')}/api/public/editor/{token}/print-source";

            // A dictionary rather than an anonymous type because the same payload must be both
            // signed and sent, and the signature has to cover exactly what is transmitted.
            var request = new Dictionary<string, object?>
            {
                ["async"] = false,
                ["filetype"] = "docx",
                ["outputtype"] = "pdf",
                ["key"] = conversionId.ToString("N"),
                ["title"] = $"{conversionId:N}.docx",
                ["url"] = sourceUrl,
            };

            // OnlyOffice rejects unsigned requests whenever JWT_ENABLED is true on the document
            // server — which it should be, or anyone who can reach it can convert anything. The
            // rejection arrives as an HTML error page, which is why the original symptom was an
            // opaque "'<' is an invalid start of a value" JSON parse failure rather than
            // anything mentioning authentication.
            //
            // Note this is a real JWT, distinct from IEditorTokenService's tokens: those are a
            // custom payload.signature format for DMS's own file URLs, and OnlyOffice would not
            // accept one. Same secret, different format.
            var signed = new Dictionary<string, object?>(request)
            {
                ["token"] = OnlyOfficeJwt.Sign(request, settings.TokenSecret),
            };

            // async:false asks the service to block until finished, but it may still answer
            // with partial progress under load, so this polls rather than assuming one call
            // suffices.
            for (var attempt = 0; attempt < 30; attempt++)
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, conversionUrl)
                {
                    Content = JsonContent.Create(signed),
                };

                // Some document-server builds check the Authorization header rather than the
                // body token, and accept either. Sending both costs nothing and avoids a
                // version-dependent failure that would look identical to a misconfiguration.
                //
                // Built as a local rather than inline in the interpolation: a collection
                // initializer's braces inside an interpolated string is at best hard to read
                // and at worst ambiguous to the parser.
                var headerPayload = new Dictionary<string, object?> { ["payload"] = request };
                var headerToken = OnlyOfficeJwt.Sign(headerPayload, settings.TokenSecret);

                message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {headerToken}");

                var response = await client.SendAsync(message, cancellationToken);

                // Read as text first, deliberately. The conversion service answers with JSON
                // when it is reached correctly — but a proxy error page, an OnlyOffice error
                // page, or a redirect to a login all arrive as HTML, and parsing those as JSON
                // produced only "'<' is an invalid start of a value", which says nothing about
                // what actually went wrong. Keeping the raw body means the message can carry it.
                var raw = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        $"The document server returned {(int)response.StatusCode} "
                        + $"{response.ReasonPhrase} from {conversionUrl}. Body: {Excerpt(raw)}");
                }

                JsonElement payload;
                try
                {
                    payload = JsonSerializer.Deserialize<JsonElement>(raw);
                }
                catch (JsonException)
                {
                    throw new InvalidOperationException(
                        $"The document server replied to {conversionUrl} with something that "
                        + $"isn't JSON, which usually means the request never reached the "
                        + $"conversion service — a proxy error page, a redirect, or the wrong "
                        + $"URL. Body: {Excerpt(raw)}");
                }

                if (payload.TryGetProperty("error", out var error))
                {
                    throw new InvalidOperationException(
                        $"The document server rejected the conversion (error {error}). Common "
                        + "causes: the staging URL isn't reachable from the document server "
                        + "(check DocumentServer:CallbackBaseUrl), or JWT is enabled there and "
                        + "the request isn't signed.");
                }

                if (payload.TryGetProperty("endConvert", out var done)
                    && done.ValueKind == JsonValueKind.True
                    && payload.TryGetProperty("fileUrl", out var fileUrl)
                    && fileUrl.GetString() is { Length: > 0 } address)
                {
                    return await client.GetByteArrayAsync(address, cancellationToken);
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }

            throw new TimeoutException(
                "The document server did not finish converting to PDF within 30 seconds.");
        }
        finally
        {
            // Always removed, success or failure. A failed conversion still leaves a full copy
            // of a controlled document behind a token-guarded public URL, and letting those
            // accumulate would be a slow leak of exactly the content this system controls.
            try
            {
                await files.DeleteAsync(stagingKey, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not remove conversion staging file {Key}.", stagingKey);
            }
        }
    }

    /// <summary>
    /// Trims a response body to something readable in an error message and a log line. An
    /// HTML error page can be tens of kilobytes; the first couple of hundred characters
    /// always contain the part that identifies it.
    /// </summary>
    private static string Excerpt(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "(empty)";
        }

        var collapsed = body.Replace("\r", " ").Replace("\n", " ").Trim();
        return collapsed.Length <= 300 ? collapsed : collapsed[..300] + "…";
    }

    /// <summary>
    /// Reads back a staging copy for the document server. Keyed by the conversion id the token
    /// carries, so a valid signed token is the only way to reach it.
    /// </summary>
    public Task<byte[]?> ReadStagedAsync(Guid conversionId, CancellationToken cancellationToken) =>
        files.ReadAsync($"{StagingPrefix}{conversionId:N}.docx", cancellationToken);
}
