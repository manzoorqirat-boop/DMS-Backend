using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Dms.Domain.Constants;

namespace Dms.Domain.Tests;

/// <summary>
/// Builds minimal WordprocessingML packages in memory.
/// <para>
/// Generated rather than committed as binary fixtures on purpose. A .docx checked into the
/// repo is opaque — nobody can see from a diff why a test broke, and "fix the fixture" usually
/// means opening Word and hoping. Built here, each test states in code exactly which structure
/// it depends on, and a test for "protection removed" differs from the passing case by one
/// visible argument.
/// </para>
/// <para>
/// These are not complete Word documents. They carry only the parts
/// <c>DocxTemplateValidator</c>, <c>DocxMetadataWriter</c> and <c>DocxProtectionVerifier</c>
/// actually read. A test asserting Word can open the result would be testing something else.
/// </para>
/// </summary>
internal static class TestDocx
{
    public const string WordNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <param name="tags">Content-control tags to emit, each with placeholder text.</param>
    /// <param name="enforceProtection">
    /// False emits <c>w:enforcement="0"</c> — the "Restrict Editing opened and abandoned" case.
    /// </param>
    /// <param name="includeSettings">False omits word/settings.xml entirely.</param>
    /// <param name="includeEditableRange">False omits the permStart/permEnd pair.</param>
    /// <param name="lockControls">
    /// True puts w:lock="sdtContentLocked" on every content control — the editor-agnostic way
    /// of protecting metadata, and the one OnlyOffice honours. A template built this way needs
    /// no document protection at all.
    /// </param>
    /// <param name="splitRuns">
    /// True splits each placeholder across two runs, which is what Word does after an edit or
    /// spellcheck. The single most likely thing to break the writer and verifier.
    /// </param>
    public static byte[] Build(
        IEnumerable<string> tags,
        bool enforceProtection = true,
        bool includeSettings = true,
        bool includeEditableRange = true,
        bool splitRuns = false,
        bool lockControls = false)
    {
        var body = new StringBuilder();

        if (includeEditableRange)
        {
            body.Append("""<w:permStart w:id="1" w:edGrp="everyone"/>""");
        }

        foreach (var tag in tags)
        {
            body.Append(ContentControl(tag, splitRuns, lockControls));
        }

        if (includeEditableRange)
        {
            body.Append("""<w:permEnd w:id="1"/>""");
        }

        var documentXml =
            $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="{WordNs}"><w:body>{body}</w:body></w:document>""";

        var entries = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["word/document.xml"] = documentXml,
        };

        if (includeSettings)
        {
            var enforcement = enforceProtection ? "1" : "0";
            entries["word/settings.xml"] =
                $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:settings xmlns:w="{WordNs}"><w:documentProtection w:edit="readOnly" w:enforcement="{enforcement}"/></w:settings>""";
        }

        // An unrelated part, so tests cover that repackaging preserves parts it doesn't touch.
        entries["word/styles.xml"] =
            $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:styles xmlns:w="{WordNs}"><w:style w:styleId="Normal"/></w:styles>""";

        return Zip(entries);
    }

    /// <summary>A template carrying the seven default tags, protected and with an editable body.</summary>
    public static byte[] ValidTemplate(bool splitRuns = false) =>
        Build(TemplateFieldTags.Required, splitRuns: splitRuns);

    /// <summary>Raw text of a named content control, as the verifier reads it.</summary>
    public static string? ReadTag(byte[] docx, string tag)
    {
        using var stream = new MemoryStream(docx);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var entry = archive.GetEntry("word/document.xml");
        if (entry is null)
        {
            return null;
        }

        using var reader = entry.Open();
        var document = XDocument.Load(reader);

        var sdt = document
            .Descendants(XName.Get("sdt", WordNs))
            .FirstOrDefault(el => el
                .Element(XName.Get("sdtPr", WordNs))?
                .Element(XName.Get("tag", WordNs))?
                .Attribute(XName.Get("val", WordNs))?.Value == tag);

        var content = sdt?.Element(XName.Get("sdtContent", WordNs));

        return content is null
            ? null
            : string.Concat(content
                .Descendants(XName.Get("t", WordNs))
                .Select(t => t.Value));
    }

    public static bool HasPart(byte[] docx, string partName)
    {
        using var stream = new MemoryStream(docx);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        return archive.GetEntry(partName) is not null;
    }

    private static string ContentControl(string tag, bool splitRuns, bool locked = false)
    {
        var placeholder = splitRuns
            ? $"""<w:r><w:t>{tag[..1]}</w:t></w:r><w:r><w:t>{tag[1..]}</w:t></w:r>"""
            : $"""<w:r><w:t>{tag}</w:t></w:r>""";

        // Unlocked by default so the default fixture exercises the document-protection branch
        // of ProtectsMetadataFields. Pass lockControls: true to exercise the per-control-lock
        // branch instead — the one OnlyOffice actually honours.
        var lockElement = locked ? """<w:lock w:val="sdtContentLocked"/>""" : "";

        return $"""<w:sdt><w:sdtPr><w:tag w:val="{tag}"/>{lockElement}</w:sdtPr><w:sdtContent><w:p>{placeholder}</w:p></w:sdtContent></w:sdt>""";
    }

    private static byte[] Zip(IReadOnlyDictionary<string, string> entries)
    {
        using var output = new MemoryStream();

        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        return output.ToArray();
    }
}
