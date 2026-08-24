using System.Xml.Linq;
using System.IO.Compression;

namespace Dms.Domain.Services;

/// <summary>
/// Re-checks a working copy that has come back from an editing session: is the document
/// protection still enforced, are all the required content controls still there, and do the
/// system-populated fields still hold the values the server wrote?
/// <para>
/// This is the second half of URS Functions #16, and the half that actually matters.
/// <see cref="DocxMetadataWriter"/> writes the metadata into locked regions before the author
/// ever sees the file — but a lock enforced only by the editor is a lock enforced by the
/// client, and the whole premise of a regulated system is that the client is not trusted.
/// Whatever the document server sends back gets checked here before it is accepted, so an
/// author who found a way to edit a protected region gets a rejected save and an audit entry
/// rather than a quietly altered controlled document.
/// </para>
/// <para>
/// Pure and I/O-free, like the validator and the writer.
/// </para>
/// </summary>
public static class DocxProtectionVerifier
{
    private const string WordNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <param name="docxBytes">The working copy as returned by the editing session.</param>
    /// <param name="expected">Tag name to the value the server wrote. Compared exactly.</param>
    public static DocxVerificationResult Verify(byte[] docxBytes, IReadOnlyDictionary<string, string> expected)
    {
        XDocument documentXml;
        XDocument? settingsXml;

        try
        {
            (documentXml, settingsXml) = LoadParts(docxBytes);
        }
        catch (FileNotFoundException)
        {
            return DocxVerificationResult.Failed(["Saved file doesn't contain word/document.xml."]);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return DocxVerificationResult.Failed([$"Saved file isn't a readable .docx: {ex.Message}"]);
        }

        var findings = new List<string>();

        if (!HasEnforcedProtection(settingsXml))
        {
            findings.Add(
                "Document protection is no longer enforced on the saved file. The header, footer "
                + "and metadata regions were unlocked at some point during editing.");
        }

        var actual = ReadContentControlValues(documentXml);

        foreach (var (tag, expectedValue) in expected)
        {
            if (!actual.TryGetValue(tag, out var actualValue))
            {
                findings.Add($"Content control '{tag}' is missing from the saved file.");
                continue;
            }

            if (!string.Equals(actualValue, expectedValue, StringComparison.Ordinal))
            {
                // The altered value is deliberately not echoed back: the finding goes into an
                // audit record, and copying attacker-controlled text verbatim into a
                // regulated trail is a poor idea. What changed is what matters; the saved file
                // is retained for inspection either way.
                findings.Add($"Content control '{tag}' was modified during editing.");
            }
        }

        return findings.Count == 0
            ? DocxVerificationResult.Passed()
            : DocxVerificationResult.Failed(findings);
    }

    private static (XDocument document, XDocument? settings) LoadParts(byte[] docxBytes)
    {
        using var stream = new MemoryStream(docxBytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var documentEntry = archive.GetEntry("word/document.xml")
            ?? throw new FileNotFoundException("word/document.xml");

        using var documentStream = documentEntry.Open();
        var document = XDocument.Load(documentStream);

        var settingsEntry = archive.GetEntry("word/settings.xml");
        XDocument? settings = null;
        if (settingsEntry is not null)
        {
            using var settingsStream = settingsEntry.Open();
            settings = XDocument.Load(settingsStream);
        }

        return (document, settings);
    }

    /// <summary>
    /// Current text of every tagged content control, with the runs inside each one
    /// concatenated — Word splits a single logical value across several runs routinely, so
    /// reading only the first would report spurious modifications.
    /// </summary>
    private static Dictionary<string, string> ReadContentControlValues(XDocument documentXml)
    {
        XName sdt = XName.Get("sdt", WordNs);
        XName sdtPr = XName.Get("sdtPr", WordNs);
        XName sdtContent = XName.Get("sdtContent", WordNs);
        XName tag = XName.Get("tag", WordNs);
        XName val = XName.Get("val", WordNs);
        XName t = XName.Get("t", WordNs);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var element in documentXml.Descendants(sdt))
        {
            var tagName = element.Element(sdtPr)?.Element(tag)?.Attribute(val)?.Value;
            if (string.IsNullOrWhiteSpace(tagName))
            {
                continue;
            }

            var content = element.Element(sdtContent);
            if (content is null)
            {
                continue;
            }

            values[tagName] = string.Concat(content.Descendants(t).Select(x => x.Value));
        }

        return values;
    }

    private static bool HasEnforcedProtection(XDocument? settingsXml)
    {
        if (settingsXml is null)
        {
            return false;
        }

        XName documentProtection = XName.Get("documentProtection", WordNs);
        XName enforcement = XName.Get("enforcement", WordNs);

        var protectionEl = settingsXml.Descendants(documentProtection).FirstOrDefault();
        if (protectionEl is null)
        {
            return false;
        }

        var enforcementValue = protectionEl.Attribute(enforcement)?.Value;
        return enforcementValue is null or "1" or "true" or "on";
    }
}

public sealed record DocxVerificationResult
{
    public required bool IsValid { get; init; }

    public required IReadOnlyList<string> Findings { get; init; }

    public static DocxVerificationResult Passed() => new() { IsValid = true, Findings = [] };

    public static DocxVerificationResult Failed(IReadOnlyList<string> findings) =>
        new() { IsValid = false, Findings = findings };
}
