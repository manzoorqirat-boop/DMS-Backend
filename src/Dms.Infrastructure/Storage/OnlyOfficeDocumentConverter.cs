using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
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
                // Without this the service answers in XML (<FileResult><FileUrl>…), its
                // default. Asking is one line; the parser still handles both, because a
                // response format changing under us should not break conversion twice.
                ["outputformat"] = "json",
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

                // ConvertService.ashx answers in XML unless JSON is explicitly requested — a
                // detail that cost a round trip to discover, because the XML it returned was a
                // perfectly successful conversion that the JSON parser rejected out of hand.
                message.Headers.TryAddWithoutValidation("Accept", "application/json");

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

                // Parsed from either format. Asking for JSON above should make this moot, but
                // the XML path stays because the Accept header is a request, not a guarantee —
                // and a version that ignores it should not break conversion again.
                var result = ParseConversionResponse(raw, conversionUrl);

                if (result.Error is { } errorCode)
                {
                    throw new InvalidOperationException(
                        $"The document server rejected the conversion (error {errorCode}). Common "
                        + "causes: the staging URL isn't reachable from the document server "
                        + "(check DocumentServer:CallbackBaseUrl), or the JWT secret doesn't "
                        + $"match its JWT_SECRET. Body: {Excerpt(raw)}");
                }

                if (result.FileUrl is { Length: > 0 } address)
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
    /// Reads a conversion response in either of the two shapes the service produces.
    /// <para>
    /// JSON when asked for it, XML otherwise: <c>&lt;FileResult&gt;&lt;FileUrl&gt;…&lt;/FileUrl&gt;&lt;/FileResult&gt;</c>,
    /// or <c>&lt;FileResult&gt;&lt;Error&gt;-4&lt;/Error&gt;&lt;/FileResult&gt;</c> on failure.
    /// Both are handled rather than only the requested one, because the Accept header is a
    /// request rather than a guarantee.
    /// </para>
    /// </summary>
    private static (string? FileUrl, string? Error) ParseConversionResponse(string raw, string conversionUrl)
    {
        var trimmed = raw.TrimStart();

        if (trimmed.StartsWith('{'))
        {
            var payload = JsonSerializer.Deserialize<JsonElement>(trimmed);

            var error = payload.TryGetProperty("error", out var e) ? e.ToString() : null;

            // endConvert false means "still working" — not an error, and not a result either.
            var url = payload.TryGetProperty("endConvert", out var done)
                      && done.ValueKind == JsonValueKind.True
                      && payload.TryGetProperty("fileUrl", out var f)
                ? f.GetString()
                : null;

            return (url, error);
        }

        if (trimmed.StartsWith('<'))
        {
            var document = XDocument.Parse(trimmed);
            var root = document.Root;

            // Element names are matched case-insensitively: the service has shipped both
            // FileUrl and fileUrl across versions, and a casing change should not read as a
            // failed conversion.
            string? Value(string name) => root?
                .Elements()
                .FirstOrDefault(x => string.Equals(x.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?
                .Value;

            return (Value("FileUrl"), Value("Error"));
        }

        throw new InvalidOperationException(
            $"The document server replied to {conversionUrl} with neither JSON nor XML, which "
            + "usually means the request never reached the conversion service — a proxy error "
            + $"page, a redirect, or the wrong URL. Body: {Excerpt(raw)}");
    }

    /// <summary>
    /// Reads the conversion service's reply in either shape it uses.
    /// <para>
    /// It answers in XML by default (<c>&lt;FileResult&gt;&lt;FileUrl&gt;…</c>) and in JSON when
    /// asked. The request asks for JSON, so the XML branch should be dead — it stays because
    /// assuming that field is honoured is exactly what produced the confusing failure first
    /// time round: a perfectly successful conversion reported as "the request never reached the
    /// conversion service", purely because the reply was XML and the parser only spoke JSON.
    /// </para>
    /// </summary>
    private static ConversionResponse ParseConversionResponse(string raw, string conversionUrl)
    {
        var trimmed = raw.TrimStart();

        if (trimmed.StartsWith('{'))
        {
            try
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(raw);

                var error = payload.TryGetProperty("error", out var e) ? e.ToString() : null;
                var url = payload.TryGetProperty("fileUrl", out var f) ? f.GetString() : null;
                var done = payload.TryGetProperty("endConvert", out var d)
                           && d.ValueKind == JsonValueKind.True;

                return new ConversionResponse(error, done ? url : null);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"The document server's JSON reply from {conversionUrl} could not be read: "
                    + $"{ex.Message}. Body: {Excerpt(raw)}");
            }
        }

        if (trimmed.StartsWith('<'))
        {
            try
            {
                var root = XDocument.Parse(raw).Root;

                var error = root?.Element("Error")?.Value;

                // XDocument decodes the &amp; entities, which matters: the cache URL carries
                // md5, expires and filename parameters, and a still-encoded ampersand would
                // collapse them into one malformed parameter.
                var url = root?.Element("FileUrl")?.Value;

                return new ConversionResponse(
                    string.IsNullOrWhiteSpace(error) ? null : error,
                    string.IsNullOrWhiteSpace(url) ? null : url);
            }
            catch (System.Xml.XmlException ex)
            {
                throw new InvalidOperationException(
                    $"The document server's XML reply from {conversionUrl} could not be read: "
                    + $"{ex.Message}. Body: {Excerpt(raw)}");
            }
        }

        throw new InvalidOperationException(
            $"The document server replied to {conversionUrl} with neither JSON nor XML, which "
            + "usually means the request never reached the conversion service — a proxy error "
            + $"page, a redirect, or the wrong URL. Body: {Excerpt(raw)}");
    }

    /// <summary>Either an error code or a finished file URL; never usefully both.</summary>
    private sealed record ConversionResponse(string? Error, string? FileUrl);

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
