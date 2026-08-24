using System.IO.Compression;
using System.Xml.Linq;

namespace Dms.Domain.Services;

/// <summary>
/// Writes system-populated metadata into a template's content controls, producing the working
/// copy an author starts from. The counterpart to <see cref="DocxTemplateValidator"/>: the
/// validator proves the <c>&lt;w:sdt&gt;</c> placeholders exist, this fills them.
/// <para>
/// URS Functions #16 says the author must not be able to type this metadata. That's enforced
/// in two halves, and this is the first: the values are written server-side, before the author
/// ever opens the document, into regions the template's own <c>documentProtection</c> excludes
/// from the editable range. The second half — revalidating on save that the author didn't
/// defeat the protection — is Phase 4, and this class does not substitute for it.
/// </para>
/// <para>
/// Pure and I/O-free beyond the bytes handed in, for the same reason as the validator: it can
/// be tested against sample .docx files with no database, no blob store and no document server
/// running.
/// </para>
/// </summary>
public static class DocxMetadataWriter
{
    private const string WordNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string XmlNs = "http://www.w3.org/XML/1998/namespace";
    private const string DocumentPart = "word/document.xml";

    /// <summary>
    /// Returns a copy of <paramref name="docxBytes"/> with each content control named in
    /// <paramref name="values"/> set to its value.
    /// </summary>
    /// <param name="docxBytes">The template file. Not modified.</param>
    /// <param name="values">Tag name to display value, as produced by <c>MetadataResolver</c>.</param>
    public static DocxMetadataWriteResult Write(byte[] docxBytes, IReadOnlyDictionary<string, string> values)
    {
        using var source = new MemoryStream(docxBytes);
        using var archive = new ZipArchive(source, ZipArchiveMode.Read);

        var documentEntry = archive.GetEntry(DocumentPart)
            ?? throw new InvalidOperationException($"Archive doesn't contain {DocumentPart}.");

        XDocument documentXml;
        using (var documentStream = documentEntry.Open())
        {
            documentXml = XDocument.Load(documentStream, LoadOptions.PreserveWhitespace);
        }

        var written = ApplyValues(documentXml, values);
        var missing = values.Keys.Where(tag => !written.Contains(tag)).ToList();

        var output = Repackage(archive, documentXml);

        return new DocxMetadataWriteResult
        {
            Content = output,
            MissingTags = missing,
        };
    }

    /// <summary>Sets each matching content control's text, returning the tags actually found.</summary>
    private static HashSet<string> ApplyValues(
        XDocument documentXml,
        IReadOnlyDictionary<string, string> values)
    {
        XName sdt = XName.Get("sdt", WordNs);
        XName sdtPr = XName.Get("sdtPr", WordNs);
        XName sdtContent = XName.Get("sdtContent", WordNs);
        XName tag = XName.Get("tag", WordNs);
        XName val = XName.Get("val", WordNs);

        var written = new HashSet<string>(StringComparer.Ordinal);

        // Materialised before mutating: the content of an sdt is rewritten in place, and
        // enumerating Descendants lazily while doing that is undefined behaviour.
        foreach (var element in documentXml.Descendants(sdt).ToList())
        {
            var tagName = element.Element(sdtPr)?.Element(tag)?.Attribute(val)?.Value;
            if (tagName is null || !values.TryGetValue(tagName, out var value))
            {
                continue;
            }

            var content = element.Element(sdtContent);
            if (content is null)
            {
                continue;
            }

            SetText(content, value);
            written.Add(tagName);
        }

        return written;
    }

    /// <summary>
    /// Replaces the visible text of a content control while keeping its formatting.
    /// <para>
    /// The first <c>w:t</c> takes the value and every later one in the same control is emptied
    /// rather than removed. Emptying keeps the surrounding run properties intact, so a control
    /// whose placeholder text happened to be split across runs — which Word does routinely,
    /// after a spellcheck or an edit — doesn't lose its font when refilled.
    /// </para>
    /// </summary>
    private static void SetText(XElement sdtContent, string value)
    {
        XName t = XName.Get("t", WordNs);
        XName r = XName.Get("r", WordNs);
        XName p = XName.Get("p", WordNs);
        XName space = XName.Get("space", XmlNs);

        var textElements = sdtContent.Descendants(t).ToList();

        if (textElements.Count == 0)
        {
            // An empty control — no run to reuse, so build the minimal valid structure. A
            // block-level control wraps its runs in a w:p; an inline one doesn't.
            var run = new XElement(r, new XElement(t, new XAttribute(space, "preserve"), value));
            var paragraph = sdtContent.Descendants(p).FirstOrDefault();

            if (paragraph is not null)
            {
                paragraph.Add(run);
            }
            else
            {
                sdtContent.Add(run);
            }

            return;
        }

        var first = textElements[0];
        first.SetAttributeValue(space, "preserve");
        first.Value = value;

        for (var i = 1; i < textElements.Count; i++)
        {
            textElements[i].Value = "";
        }
    }

    /// <summary>
    /// Rebuilds the archive with the edited document part, copying every other entry byte for
    /// byte. Rewriting only the one part matters: styles, numbering, fonts, headers, relations
    /// and the protection settings all have to survive untouched, and round-tripping them
    /// through an XML parser risks changing them in ways Word notices.
    /// </summary>
    private static byte[] Repackage(ZipArchive source, XDocument documentXml)
    {
        using var output = new MemoryStream();

        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                var copy = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using var writer = copy.Open();

                if (string.Equals(entry.FullName, DocumentPart, StringComparison.Ordinal))
                {
                    // DisableFormatting: pretty-printing would insert whitespace between run
                    // elements, and in WordprocessingML that whitespace can render as spaces.
                    documentXml.Save(writer, SaveOptions.DisableFormatting);
                }
                else
                {
                    using var reader = entry.Open();
                    reader.CopyTo(writer);
                }
            }
        }

        return output.ToArray();
    }
}

public sealed record DocxMetadataWriteResult
{
    public required byte[] Content { get; init; }

    /// <summary>
    /// Tags that were asked for but not present in the file. Should always be empty for a
    /// template that passed <see cref="DocxTemplateValidator"/> — a non-empty list means the
    /// stored bytes and the validation record have diverged, which is worth surfacing rather
    /// than shipping a working copy with blank metadata.
    /// </summary>
    public required IReadOnlyList<string> MissingTags { get; init; }
}
