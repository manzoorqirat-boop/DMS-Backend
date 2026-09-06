using System.IO.Compression;
using System.Xml.Linq;

namespace Dms.Domain.Services;

/// <summary>
/// Stamps a banner into a document's page headers, so it repeats on every printed page.
/// <para>
/// Extracted from the controlled-copy print renderer, which owned this privately until the
/// approved-PDF export needed the same thing. Two copies of header-injection XML would have
/// drifted, and the less-exercised copy's bugs would have surfaced later and stranger.
/// </para>
/// <para>
/// A header band rather than a diagonal watermark across the text, deliberately. It survives
/// photocopying and faxing legibly; it cannot obscure a procedure step, which matters when
/// someone is reading instructions off the page; and it renders identically through a document
/// converter, which has no watermark feature of its own. A thin coloured rule sits above the
/// text so the stamp is still distinguishable on a monochrome copier where the colour is lost —
/// position and wording carry it when the colour cannot.
/// </para>
/// <para>
/// Pure and I/O-free: bytes in, bytes out.
/// </para>
/// </summary>
public static class DocxPageStamper
{
    private const string WordNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>
    /// Returns a copy of <paramref name="docxBytes"/> with the stamp prepended to every header
    /// part. A document with no header part gets the stamp at the top of the body instead —
    /// once rather than per page, which is worse but far better than an unmarked document.
    /// </summary>
    /// <param name="colourHex">Six hex digits, no leading hash.</param>
    public static byte[] Stamp(byte[] docxBytes, string text, string colourHex)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return docxBytes;
        }

        using var output = new MemoryStream();

        using (var input = new MemoryStream(docxBytes))
        using (var source = new ZipArchive(input, ZipArchiveMode.Read))
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var headerParts = source.Entries
                .Where(e => e.FullName.StartsWith("word/header", StringComparison.Ordinal)
                            && e.FullName.EndsWith(".xml", StringComparison.Ordinal))
                .Select(e => e.FullName)
                .ToHashSet(StringComparer.Ordinal);

            var stampBody = headerParts.Count == 0;

            foreach (var entry in source.Entries)
            {
                var copy = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using var reader = entry.Open();
                using var writer = copy.Open();

                if (headerParts.Contains(entry.FullName))
                {
                    writer.Write(PrependTo(reader, text, colourHex, "hdr"));
                }
                else if (stampBody && entry.FullName == "word/document.xml")
                {
                    writer.Write(PrependTo(reader, text, colourHex, "body"));
                }
                else
                {
                    reader.CopyTo(writer);
                }
            }
        }

        return output.ToArray();
    }

    private static byte[] PrependTo(Stream partStream, string text, string colourHex, string container)
    {
        var xml = XDocument.Load(partStream);
        XNamespace w = WordNs;

        // In a header the root itself holds the content; in document.xml the body does.
        var host = container == "body"
            ? xml.Root?.Element(w + "body")
            : xml.Root;

        if (host is null)
        {
            using var untouched = new MemoryStream();
            xml.Save(untouched, SaveOptions.DisableFormatting);
            return untouched.ToArray();
        }

        // Added in reverse so the rule ends up above the text.
        host.AddFirst(StampParagraph(w, text, colourHex));
        host.AddFirst(Rule(w, colourHex));

        using var result = new MemoryStream();
        xml.Save(result, SaveOptions.DisableFormatting);
        return result.ToArray();
    }

    /// <summary>
    /// A thin coloured bar, drawn as a bottom-bordered empty paragraph. Cheaper and far more
    /// portable than a drawing-layer shape, which converters render inconsistently.
    /// </summary>
    private static XElement Rule(XNamespace w, string colourHex) =>
        new(w + "p",
            new XElement(w + "pPr",
                new XElement(w + "pBdr",
                    new XElement(w + "bottom",
                        new XAttribute(w + "val", "single"),
                        new XAttribute(w + "sz", "18"),
                        new XAttribute(w + "space", "0"),
                        new XAttribute(w + "color", colourHex))),
                new XElement(w + "spacing",
                    new XAttribute(w + "before", "0"),
                    new XAttribute(w + "after", "0"),
                    new XAttribute(w + "line", "20"),
                    new XAttribute(w + "lineRule", "exact"))));

    private static XElement StampParagraph(XNamespace w, string text, string colourHex) =>
        new(w + "p",
            new XElement(w + "pPr",
                new XElement(w + "jc", new XAttribute(w + "val", "center")),
                new XElement(w + "spacing",
                    new XAttribute(w + "before", "40"),
                    new XAttribute(w + "after", "80"))),
            new XElement(w + "r",
                new XElement(w + "rPr",
                    new XElement(w + "b"),
                    new XElement(w + "caps"),
                    new XElement(w + "color", new XAttribute(w + "val", colourHex)),
                    new XElement(w + "sz", new XAttribute(w + "val", "18"))),
                new XElement(w + "t",
                    new XAttribute(XNamespace.Xml + "space", "preserve"), text)));
}
