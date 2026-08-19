using System.IO.Compression;
using System.Xml.Linq;
using Dms.Domain.Constants;

namespace Dms.Domain.Services;

/// <summary>
/// Structural validation for an uploaded .docx template. Checks that the two invariants the
/// authoring flow depends on actually hold in the file, rather than trusting whoever built
/// the template in Word to have gotten it right:
/// <list type="number">
///   <item>every system-populated metadata field (<see cref="TemplateFieldTags.Required"/>)
///   is present as a content control, so the merge step has somewhere to write; and</item>
///   <item>document protection is genuinely enforced, with an actual editable range carved
///   out for the body — not just a template that looks locked in the Word UI while every
///   region stays editable underneath once "Restrict Editing" is closed without being
///   applied.</item>
/// </list>
/// <para>
/// Pure and I/O-free beyond reading the bytes it's handed — no blob storage, no HTTP calls.
/// That's what makes it unit-testable against a folder of good and bad sample .docx files
/// without a database or a document server running. Uploading, storing, and wiring this into
/// <see cref="Dms.Domain.Entities.DocumentTemplate.RecordValidation"/> is an application-layer concern;
/// this class only answers "is this template's XML structurally sound".
/// </para>
/// </summary>
public static class DocxTemplateValidator
{
    private const string WordNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static TemplateValidationResult Validate(byte[] docxBytes)
    {
        XDocument documentXml;
        XDocument? settingsXml;

        try
        {
            (documentXml, settingsXml) = LoadParts(docxBytes);
        }
        catch (FileNotFoundException)
        {
            return TemplateValidationResult.Failed(
                ["Archive doesn't contain word/document.xml — this isn't a Word document."]);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return TemplateValidationResult.Failed(
                [$"Not a readable .docx file (couldn't open as a zip archive): {ex.Message}"]);
        }

        var issues = new List<string>();

        var foundTags = FindContentControlTags(documentXml);
        var missingTags = TemplateFieldTags.Required.Where(tag => !foundTags.Contains(tag)).ToList();
        if (missingTags.Count > 0)
        {
            issues.Add(
                $"Missing required content control(s): {string.Join(", ", missingTags)}. " +
                "Each must exist as a Developer > Rich Text/Plain Text Content Control with " +
                "a matching Tag value.");
        }

        if (!HasEnforcedDocumentProtection(settingsXml))
        {
            issues.Add(
                "Document protection isn't enforced (Restrict Editing > 'Yes, Start Enforcing " +
                "Protection' was never applied, or the file has no word/settings.xml at all). " +
                "Without this, the header/footer/metadata lock has no effect once the file " +
                "leaves Word.");
        }

        if (!HasUnrestrictedRange(documentXml))
        {
            issues.Add(
                "No unrestricted editing range found (no matching w:permStart/w:permEnd " +
                "pair). Protecting the whole document with no exception means the author has " +
                "no body left to write in.");
        }

        return issues.Count == 0
            ? TemplateValidationResult.Passed()
            : TemplateValidationResult.Failed(issues);
    }

    private static (XDocument document, XDocument? settings) LoadParts(byte[] docxBytes)
    {
        using var stream = new MemoryStream(docxBytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var documentEntry = archive.GetEntry("word/document.xml")
            ?? throw new FileNotFoundException("word/document.xml");

        using var documentStream = documentEntry.Open();
        var document = XDocument.Load(documentStream);

        // documentProtection lives in settings.xml, not document.xml. Its absence is itself a
        // validation finding (unprotected template), not a load error.
        var settingsEntry = archive.GetEntry("word/settings.xml");
        XDocument? settings = null;
        if (settingsEntry is not null)
        {
            using var settingsStream = settingsEntry.Open();
            settings = XDocument.Load(settingsStream);
        }

        return (document, settings);
    }

    /// <summary>Every distinct w:tag/@w:val found on a w:sdt anywhere in the document.</summary>
    private static HashSet<string> FindContentControlTags(XDocument documentXml)
    {
        XName sdt = XName.Get("sdt", WordNs);
        XName sdtPr = XName.Get("sdtPr", WordNs);
        XName tag = XName.Get("tag", WordNs);
        XName val = XName.Get("val", WordNs);

        return documentXml.Descendants(sdt)
            .Select(el => el.Element(sdtPr)?.Element(tag)?.Attribute(val)?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// True only if protection is both declared and switched on. w:enforcement="0" (or a
    /// missing settings part entirely) means "Restrict Editing" was opened and abandoned
    /// without clicking through — a half-finished attempt, not a working lock.
    /// </summary>
    private static bool HasEnforcedDocumentProtection(XDocument? settingsXml)
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

        // OOXML boolean attributes accept "1"/"0", "true"/"false", or bare presence meaning true.
        return enforcementValue is null or "1" or "true" or "on";
    }

    /// <summary>At least one well-formed w:permStart/w:permEnd pair, matched by w:id.</summary>
    private static bool HasUnrestrictedRange(XDocument documentXml)
    {
        XName permStart = XName.Get("permStart", WordNs);
        XName permEnd = XName.Get("permEnd", WordNs);
        XName id = XName.Get("id", WordNs);

        var startIds = documentXml.Descendants(permStart)
            .Select(el => el.Attribute(id)?.Value)
            .Where(v => v is not null)
            .Select(v => v!)
            .ToHashSet(StringComparer.Ordinal);

        var endIds = documentXml.Descendants(permEnd)
            .Select(el => el.Attribute(id)?.Value)
            .Where(v => v is not null)
            .Select(v => v!)
            .ToHashSet(StringComparer.Ordinal);

        return startIds.Overlaps(endIds);
    }
}

/// <summary>Result of <see cref="DocxTemplateValidator.Validate"/>.</summary>
public sealed record TemplateValidationResult
{
    public required bool IsValid { get; init; }
    public required IReadOnlyList<string> Issues { get; init; }

    public static TemplateValidationResult Passed() => new() { IsValid = true, Issues = [] };

    public static TemplateValidationResult Failed(IReadOnlyList<string> issues) =>
        new() { IsValid = false, Issues = issues };
}
