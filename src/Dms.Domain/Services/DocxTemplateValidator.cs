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
///   <item>those metadata fields are actually protected from being edited by the author —
///   by per-control locks or by enforced document protection with a body exception, either
///   being sufficient. See <c>ProtectsMetadataFields</c> for why this checks the property
///   rather than one particular mechanism.</item>
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

    /// <summary>
    /// Validates against the fixed default tag set. Retained for callers that have no document
    /// type in hand — anything type-aware should use the overload taking explicit tags, since
    /// which fields a type requires is configuration now, not a constant.
    /// </summary>
    public static TemplateValidationResult Validate(byte[] docxBytes) =>
        Validate(docxBytes, TemplateFieldTags.Required);

    /// <param name="requiredTags">
    /// Content-control tags the template must declare, from the document type's configured
    /// metadata field definitions.
    /// </param>
    public static TemplateValidationResult Validate(byte[] docxBytes, IReadOnlyList<string> requiredTags)
    {
        XDocument documentXml;
        IReadOnlyList<XDocument> headerXml;
        XDocument? settingsXml;

        try
        {
            (documentXml, headerXml, settingsXml) = LoadParts(docxBytes);
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

        // Body and headers together: a control satisfies the requirement wherever it sits.
        var contentParts = new List<XDocument>(headerXml) { documentXml };
        var foundTags = FindContentControlTags(contentParts);
        var missingTags = requiredTags.Where(tag => !foundTags.Contains(tag)).ToList();
        if (missingTags.Count > 0)
        {
            issues.Add(
                $"Missing required content control(s): {string.Join(", ", missingTags)}. " +
                "Each must exist as a Developer > Rich Text/Plain Text Content Control with " +
                "a matching Tag value.");
        }

        // The invariant that actually matters: an author must not be able to overwrite the
        // system-populated metadata. Two different mechanisms satisfy it, and either is
        // accepted — see the remarks on ProtectsMetadataFields for why requiring one specific
        // mechanism turned out to be the wrong test.
        if (!ProtectsMetadataFields(contentParts, documentXml, settingsXml, requiredTags, out var unlockedTags))
        {
            issues.Add(
                "Metadata fields aren't protected from editing. Either lock each metadata " +
                "content control individually (Developer > Properties > \"Contents cannot be " +
                "edited\"), or enforce document protection with an editing range for the body " +
                "(Restrict Editing > 'Yes, Start Enforcing Protection'). " +
                (unlockedTags.Count > 0
                    ? $"Currently unlocked: {string.Join(", ", unlockedTags)}."
                    : "Neither was found in this file."));
        }

        return issues.Count == 0
            ? TemplateValidationResult.Passed()
            : TemplateValidationResult.Failed(issues);
    }

    private static (XDocument document, IReadOnlyList<XDocument> headers, XDocument? settings)
        LoadParts(byte[] docxBytes)
    {
        using var stream = new MemoryStream(docxBytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var documentEntry = archive.GetEntry("word/document.xml")
            ?? throw new FileNotFoundException("word/document.xml");

        using var documentStream = documentEntry.Open();
        var document = XDocument.Load(documentStream);

        // Header parts are read too, because a controlled document's metadata belongs in a
        // page header: that is what makes the document number and revision repeat on every
        // printed page rather than appearing once and being lost the moment someone
        // photocopies page 7. Previously only word/document.xml was inspected, so a template
        // built the correct way failed validation for "missing" controls that were present —
        // and DocxMetadataWriter, which had the same blind spot, would never have filled them.
        var headers = new List<XDocument>();
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith("word/header", StringComparison.Ordinal)
                || !entry.FullName.EndsWith(".xml", StringComparison.Ordinal))
            {
                continue;
            }

            using var headerStream = entry.Open();
            headers.Add(XDocument.Load(headerStream));
        }

        // documentProtection lives in settings.xml, not document.xml. Its absence is itself a
        // validation finding (unprotected template), not a load error.
        var settingsEntry = archive.GetEntry("word/settings.xml");
        XDocument? settings = null;
        if (settingsEntry is not null)
        {
            using var settingsStream = settingsEntry.Open();
            settings = XDocument.Load(settingsStream);
        }

        return (document, headers, settings);
    }

    /// <summary>Every distinct w:tag/@w:val found on a w:sdt anywhere in the document.</summary>
    private static HashSet<string> FindContentControlTags(IEnumerable<XDocument> parts)
    {
        XName sdt = XName.Get("sdt", WordNs);
        XName sdtPr = XName.Get("sdtPr", WordNs);
        XName tag = XName.Get("tag", WordNs);
        XName val = XName.Get("val", WordNs);

        return parts.SelectMany(part => part.Descendants(sdt))
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
    /// <summary>
    /// Whether the template stops an author editing the system-populated metadata fields.
    /// <para>
    /// Accepts <b>either</b> of two mechanisms, because they achieve the same thing and
    /// different editors support them differently:
    /// </para>
    /// <list type="number">
    ///   <item><b>Per-control locks.</b> Every required content control carries
    ///   <c>w:lock</c> = <c>sdtContentLocked</c> or <c>sdtLocked</c>. The body stays freely
    ///   editable because nothing is locked document-wide.</item>
    ///   <item><b>Document protection with an exception range.</b> Enforced
    ///   <c>w:documentProtection</c> plus a matching <c>w:permStart</c>/<c>w:permEnd</c> pair
    ///   carving the body out. This is what Word's Restrict Editing produces.</item>
    /// </list>
    /// <para>
    /// This previously demanded mechanism 2 exclusively, which was a mistake worth recording:
    /// it tested <i>how</i> a template was protected rather than <i>whether</i> it was.
    /// Word honours document protection with its exceptions faithfully, but OnlyOffice — which
    /// is the authoring tool here, precisely because URS #13 forbids the file reaching a
    /// client PC — applies the restriction and ignores the exception. A template built for
    /// mechanism 2 therefore validated cleanly and then left the author unable to type
    /// anywhere, which is the opposite of what the check existed to guarantee.
    /// </para>
    /// <para>
    /// Mechanism 1 is honoured by both editors and is the better fit now that documents are
    /// authored server-side; mechanism 2 stays accepted so templates already built in Word
    /// don't suddenly fail. <paramref name="unlockedTags"/> names the controls missing a lock,
    /// so the message can say what to fix rather than only that something is wrong.
    /// </para>
    /// </summary>
    private static bool ProtectsMetadataFields(
        IReadOnlyList<XDocument> contentParts,
        XDocument documentXml,
        XDocument? settingsXml,
        IReadOnlyList<string> requiredTags,
        out IReadOnlyList<string> unlockedTags)
    {
        unlockedTags = FindUnlockedRequiredControls(contentParts, requiredTags);

        // Mechanism 1: every required control individually locked. An empty required-tag list
        // trivially satisfies this, which is correct — there's no metadata to protect.
        if (unlockedTags.Count == 0)
        {
            return true;
        }

        // Mechanism 2: Word-style document protection with a body exception.
        return HasEnforcedDocumentProtection(settingsXml) && HasUnrestrictedRange(documentXml);
    }

    /// <summary>
    /// Required tags whose content control carries no edit lock. A control that isn't present
    /// at all is not reported here — the missing-controls check already covers that, and
    /// naming it twice would just make the failure harder to read.
    /// </summary>
    private static IReadOnlyList<string> FindUnlockedRequiredControls(
        IReadOnlyList<XDocument> contentParts,
        IReadOnlyList<string> requiredTags)
    {
        XName sdt = XName.Get("sdt", WordNs);
        XName sdtPr = XName.Get("sdtPr", WordNs);
        XName tag = XName.Get("tag", WordNs);
        XName lockEl = XName.Get("lock", WordNs);
        XName val = XName.Get("val", WordNs);

        var lockedTags = new HashSet<string>(StringComparer.Ordinal);

        foreach (var element in contentParts.SelectMany(part => part.Descendants(sdt)))
        {
            var properties = element.Element(sdtPr);
            var tagValue = properties?.Element(tag)?.Attribute(val)?.Value;
            if (string.IsNullOrEmpty(tagValue))
            {
                continue;
            }

            var lockValue = properties?.Element(lockEl)?.Attribute(val)?.Value;

            // sdtContentLocked: contents can't be edited. sdtLocked: the control can't be
            // deleted. contentLocked is the pair of both. Anything that locks the CONTENT
            // counts; a control that's merely undeletable still lets its text be overwritten,
            // so "sdtLocked" alone deliberately does not.
            if (lockValue is "sdtContentLocked" or "contentLocked")
            {
                lockedTags.Add(tagValue);
            }
        }

        var presentTags = FindContentControlTags(contentParts);

        return requiredTags
            .Where(t => presentTags.Contains(t) && !lockedTags.Contains(t))
            .ToList();
    }

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
