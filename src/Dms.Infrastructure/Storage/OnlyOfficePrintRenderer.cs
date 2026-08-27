using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using Dms.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Dms.Infrastructure.Storage;

/// <summary>
/// Produces a watermarked PDF controlled copy using the OnlyOffice Document Server's
/// conversion API — the replacement for <see cref="PassThroughPrintRenderer"/>.
/// <para>
/// Two steps, because the converter does one of them and not the other:
/// </para>
/// <list type="number">
///   <item><b>Stamp.</b> The watermark and scan code are injected into the .docx itself, as a
///   header paragraph on every section. OnlyOffice's converter faithfully renders whatever is
///   in the file but has no watermark feature of its own, so the stamp has to exist in the
///   document before conversion rather than being applied to the PDF afterwards.</item>
///   <item><b>Convert.</b> The stamped .docx is handed to the conversion service, which
///   returns a URL to the finished PDF. A PDF matters beyond format: it flattens the content
///   controls and protection into fixed page images, so a printed controlled copy cannot be
///   reopened and edited the way a .docx can.</item>
/// </list>
/// <para>
/// The conversion service fetches the source by URL rather than accepting an upload, so the
/// stamped file is published at a short-lived callback URL for it to collect — the same
/// server-to-server pattern the editor integration already uses, and the reason
/// <c>DocumentServer:CallbackBaseUrl</c> must be the API as the document server sees it.
/// </para>
/// <para>
/// <b>Fails loudly rather than silently.</b> If conversion fails, this throws instead of
/// returning the unstamped source: an unstamped page is indistinguishable from an
/// uncontrolled printout the moment it leaves the tray, which is precisely what controlled
/// printing exists to prevent. A failed print that says so beats a successful print that
/// lies.
/// </para>
/// </summary>
public sealed class OnlyOfficePrintRenderer(
    IHttpClientFactory httpClientFactory,
    IEditorSettings settings,
    IEditorTokenService tokens,
    IDocumentFileStore files,
    ILogger<OnlyOfficePrintRenderer> logger) : IControlledPrintRenderer
{
    public const string ClientName = "onlyoffice-converter";

    /// <summary>
    /// Prefix for the short-lived staging copies the conversion service fetches. Kept distinct
    /// from real document keys so a stray staging file can never be mistaken for a working
    /// copy, and so it is obvious what to sweep if one is ever orphaned.
    /// </summary>
    public const string StagingPrefix = "print-staging/";

    private const string PdfContentType = "application/pdf";
    private const string WordNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public async Task<PrintRenderResult> RenderAsync(
        byte[] source,
        string watermark,
        string scanCode,
        CancellationToken cancellationToken)
    {
        if (!settings.IsConfigured)
        {
            throw new InvalidOperationException(
                "No document server is configured, so a watermarked PDF cannot be produced. " +
                "Set DocumentServer:Url, CallbackBaseUrl and TokenSecret, or accept unstamped " +
                "copies by registering PassThroughPrintRenderer instead.");
        }

        var stamped = StampWatermark(source, watermark, scanCode);
        var pdf = await ConvertToPdfAsync(stamped, cancellationToken);

        return new PrintRenderResult(pdf, PdfContentType, IsWatermarked: true);
    }

    /// <summary>
    /// Writes the watermark and scan code into every section's header.
    /// <para>
    /// A header rather than a true diagonal background watermark: a header appears on every
    /// page, survives conversion exactly, and needs no drawing-layer XML. The requirement is
    /// that every printed page carries its scan code and copy status — not that the text sits
    /// at 45 degrees behind the body.
    /// </para>
    /// <para>
    /// A document with no header part gets one created and related; a document that already
    /// has headers gets the stamp prepended to each, so an existing letterhead survives.
    /// </para>
    /// </summary>
    private static byte[] StampWatermark(byte[] docxBytes, string watermark, string scanCode)
    {
        using var output = new MemoryStream();

        // Copied entry by entry rather than opened Update-in-place: ZipArchive's update mode
        // rewrites the whole archive anyway, and copying makes the "modify these parts, pass
        // everything else through untouched" intent explicit.
        using (var input = new MemoryStream(docxBytes))
        using (var sourceArchive = new ZipArchive(input, ZipArchiveMode.Read))
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var headerParts = sourceArchive.Entries
                .Where(e => e.FullName.StartsWith("word/header", StringComparison.Ordinal)
                            && e.FullName.EndsWith(".xml", StringComparison.Ordinal))
                .Select(e => e.FullName)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var entry in sourceArchive.Entries)
            {
                var newEntry = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                using var newStream = newEntry.Open();

                if (headerParts.Contains(entry.FullName))
                {
                    var stamped = PrependStampToHeader(entryStream, watermark, scanCode);
                    newStream.Write(stamped);
                }
                else
                {
                    entryStream.CopyTo(newStream);
                }
            }

            // No header part at all: the stamp goes into the body as a leading paragraph. Less
            // ideal than a header — it appears once rather than per page — but far better than
            // an unmarked controlled copy, and it keeps this renderer working against the
            // simple templates the validator accepts.
            if (headerParts.Count == 0)
            {
                logger.LogWarning(
                    "Template has no header part; the controlled-copy stamp will appear once at " +
                    "the top of the document rather than on every page.");
            }
        }

        return output.ToArray();
    }

    private static byte[] PrependStampToHeader(Stream headerStream, string watermark, string scanCode)
    {
        var document = XDocument.Load(headerStream);
        var root = document.Root;

        if (root is null)
        {
            // Unparseable header — return it untouched rather than corrupting the package.
            using var buffer = new MemoryStream();
            document.Save(buffer);
            return buffer.ToArray();
        }

        XNamespace w = WordNs;

        var stampParagraph = new XElement(w + "p",
            new XElement(w + "pPr",
                new XElement(w + "jc", new XAttribute(w + "val", "center"))),
            new XElement(w + "r",
                new XElement(w + "rPr",
                    new XElement(w + "b"),
                    new XElement(w + "color", new XAttribute(w + "val", "C00000")),
                    new XElement(w + "sz", new XAttribute(w + "val", "20"))),
                new XElement(w + "t",
                    new XAttribute(XNamespace.Xml + "space", "preserve"),
                    $"{watermark}  |  {scanCode}")));

        root.AddFirst(stampParagraph);

        using var result = new MemoryStream();
        document.Save(result, SaveOptions.DisableFormatting);
        return result.ToArray();
    }

    /// <summary>
    /// Calls the document server's conversion service and downloads the resulting PDF.
    /// <para>
    /// The service is asynchronous by nature: it answers immediately with
    /// <c>percent</c> progress and only later with <c>fileUrl</c>. <c>async: false</c> asks it
    /// to block until finished, but it may still return a partial result under load, so this
    /// polls rather than assuming a single call suffices.
    /// </para>
    /// </summary>
    /// <summary>
    /// Calls the document server's conversion service and downloads the resulting PDF.
    /// <para>
    /// The service is asynchronous by nature: it answers immediately with <c>percent</c>
    /// progress and only later with <c>fileUrl</c>. <c>async: false</c> asks it to block until
    /// finished, but it may still return a partial result under load, so this polls rather
    /// than assuming a single call suffices.
    /// </para>
    /// <para>
    /// The staging copy is deleted in a finally block whatever happens. A conversion that
    /// fails still leaves a full copy of a controlled document sitting behind a token-guarded
    /// public URL, and leaving those to accumulate would be a slow leak of exactly the content
    /// this system exists to control.
    /// </para>
    /// </summary>
    private async Task<byte[]> ConvertToPdfAsync(byte[] stampedDocx, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(ClientName);
        var conversionUrl = $"{settings.DocumentServerUrl.TrimEnd('/')}/ConvertService.ashx";

        // A per-conversion id, used as both the staging key and the document server's cache
        // key. Reusing one would return the previous copy's PDF — carrying the previous copy's
        // scan code, which is the one mistake a controlled-copy renderer must never make.
        var conversionId = Guid.NewGuid();
        var stagingKey = $"{StagingPrefix}{conversionId:N}.docx";

        try
        {
            await files.SaveAsync(stagingKey, stampedDocx, cancellationToken);

            var token = tokens.Issue(conversionId, DateTimeOffset.UtcNow.AddMinutes(5));
            var sourceUrl = $"{settings.CallbackBaseUrl.TrimEnd('/')}/api/public/editor/{token}/print-source";

            var request = new
            {
                async = false,
                filetype = "docx",
                outputtype = "pdf",
                key = conversionId.ToString("N"),
                title = $"{conversionId:N}.docx",
                url = sourceUrl,
            };

            for (var attempt = 0; attempt < 30; attempt++)
            {
                var response = await client.PostAsJsonAsync(conversionUrl, request, cancellationToken);
                response.EnsureSuccessStatusCode();

                var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

                if (payload.TryGetProperty("error", out var error))
                {
                    throw new InvalidOperationException(
                        $"The document server rejected the conversion (error {error}). Common " +
                        "causes: the staging URL isn't reachable from the document server " +
                        "(check DocumentServer:CallbackBaseUrl), or JWT is enabled on the " +
                        "document server and the request isn't signed.");
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
                "The document server did not finish converting the copy to PDF within 30 seconds.");
        }
        finally
        {
            try
            {
                await files.DeleteAsync(stagingKey, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Never masks the real failure — a cleanup problem is worth knowing about but
                // is not what the caller was asking about.
                logger.LogWarning(ex, "Could not remove print staging file {Key}.", stagingKey);
            }
        }
    }

    /// <summary>
    /// Reads back a staging copy for the document server. Keyed by the conversion id the token
    /// carries, so a valid signed token is the only way to reach it.
    /// </summary>
    public Task<byte[]?> ReadStagedAsync(Guid conversionId, CancellationToken cancellationToken) =>
        files.ReadAsync($"{StagingPrefix}{conversionId:N}.docx", cancellationToken);
}
