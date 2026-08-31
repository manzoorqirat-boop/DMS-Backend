using System.IO.Compression;
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
    IEditorSettings settings,
    IDocumentConverter converter,
    ILogger<OnlyOfficePrintRenderer> logger) : IControlledPrintRenderer
{


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
    private byte[] StampWatermark(byte[] docxBytes, string watermark, string scanCode)
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

            // With no header part there is nowhere to put a per-page stamp, so the stamp goes
            // into the body instead — once, at the top. Worse than a header, and said so out
            // loud, but far better than handing someone a completely unmarked controlled copy.
            // Templates produced against the current validator have no header, so this is the
            // path that actually runs today rather than a defensive corner.
            var stampBody = headerParts.Count == 0;

            if (stampBody)
            {
                logger.LogWarning(
                    "Template has no header part; the controlled-copy stamp will appear once at " +
                    "the top of the document rather than on every page.");
            }

            foreach (var entry in sourceArchive.Entries)
            {
                var newEntry = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                using var newStream = newEntry.Open();

                if (headerParts.Contains(entry.FullName))
                {
                    newStream.Write(PrependStampToHeader(entryStream, watermark, scanCode));
                }
                else if (stampBody && entry.FullName == "word/document.xml")
                {
                    newStream.Write(PrependStampToBody(entryStream, watermark, scanCode));
                }
                else
                {
                    entryStream.CopyTo(newStream);
                }
            }
        }

        return output.ToArray();
    }

    /// <summary>
    /// Inserts the stamp as the first paragraph of the document body, for templates with no
    /// header part to stamp instead.
    /// </summary>
    private static byte[] PrependStampToBody(Stream documentStream, string watermark, string scanCode)
    {
        var document = XDocument.Load(documentStream);
        XNamespace w = WordNs;

        var body = document.Root?.Element(w + "body");
        if (body is null)
        {
            using var untouched = new MemoryStream();
            document.Save(untouched, SaveOptions.DisableFormatting);
            return untouched.ToArray();
        }

        body.AddFirst(StampParagraph(watermark, scanCode));

        using var result = new MemoryStream();
        document.Save(result, SaveOptions.DisableFormatting);
        return result.ToArray();
    }

    /// <summary>The stamp itself: centred, bold, red, small. Shared by both placements.</summary>
    private static XElement StampParagraph(string watermark, string scanCode)
    {
        XNamespace w = WordNs;

        return new XElement(w + "p",
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

        root.AddFirst(StampParagraph(watermark, scanCode));

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
    /// <summary>
    /// Delegates to <see cref="IDocumentConverter"/> rather than owning the staging and
    /// polling protocol, which it previously duplicated. Two copies of that protocol would
    /// have drifted, with the less-exercised copy's bugs surfacing later and stranger.
    /// </summary>
    private Task<byte[]> ConvertToPdfAsync(byte[] stampedDocx, CancellationToken cancellationToken) =>
        converter.ToPdfAsync(stampedDocx, cancellationToken);
}
