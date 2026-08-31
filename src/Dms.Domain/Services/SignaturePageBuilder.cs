using System.IO.Compression;
using System.Xml.Linq;
using Dms.Domain.Entities;

namespace Dms.Domain.Services;

/// <summary>
/// Appends a signature manifest page to a copy of an approved document.
/// <para>
/// The page is added to the <c>.docx</c> before conversion rather than being drawn onto the
/// finished PDF. That is deliberate: whatever converts the document renders this page with the
/// same fonts and pagination as the rest of it, so the manifest cannot end up looking like
/// something pasted on afterwards — which is exactly how a forged one would look.
/// </para>
/// <para>
/// §11.50(a) requires the printed name, the date and time, and the meaning of each signature
/// to appear <i>with</i> the signed record. A PDF whose last page carries all three, for every
/// signature, is what makes the exported artefact self-contained: someone reading it offline
/// can see who approved it and in what capacity without access to this system.
/// </para>
/// <para>
/// Pure and I/O-free, like the other domain services — bytes in, bytes out.
/// </para>
/// </summary>
public static class SignaturePageBuilder
{
    private const string WordNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>
    /// Returns a copy of <paramref name="approvedDocx"/> with a signature page appended.
    /// </summary>
    /// <param name="signatures">
    /// In signing order. Rejections are included deliberately — a document that was returned
    /// for rework and later approved has a fuller story than its final approval alone, and
    /// hiding the earlier rejection would make the manifest a summary rather than a record.
    /// </param>
    public static byte[] Append(
        byte[] approvedDocx,
        ControlledDocument document,
        IReadOnlyList<ElectronicSignature> signatures)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(signatures);

        using var output = new MemoryStream();

        using (var input = new MemoryStream(approvedDocx))
        using (var source = new ZipArchive(input, ZipArchiveMode.Read))
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                var copy = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using var reader = entry.Open();
                using var writer = copy.Open();

                if (entry.FullName == "word/document.xml")
                {
                    writer.Write(AppendToBody(reader, document, signatures));
                }
                else
                {
                    reader.CopyTo(writer);
                }
            }
        }

        return output.ToArray();
    }

    private static byte[] AppendToBody(
        Stream documentStream,
        ControlledDocument document,
        IReadOnlyList<ElectronicSignature> signatures)
    {
        var xml = XDocument.Load(documentStream);
        XNamespace w = WordNs;

        var body = xml.Root?.Element(w + "body");
        if (body is null)
        {
            using var untouched = new MemoryStream();
            xml.Save(untouched, SaveOptions.DisableFormatting);
            return untouched.ToArray();
        }

        // The final sectPr carries the page setup and must stay last in the body. New content
        // goes before it, or the appended page inherits no margins and Word treats the file as
        // malformed.
        var sectPr = body.Elements(w + "sectPr").LastOrDefault();

        var content = BuildPage(w, document, signatures).ToList();

        if (sectPr is not null)
        {
            sectPr.AddBeforeSelf(content);
        }
        else
        {
            body.Add(content);
        }

        using var result = new MemoryStream();
        xml.Save(result, SaveOptions.DisableFormatting);
        return result.ToArray();
    }

    private static IEnumerable<XElement> BuildPage(
        XNamespace w,
        ControlledDocument document,
        IReadOnlyList<ElectronicSignature> signatures)
    {
        // Hard page break: the manifest starts a page of its own so it is never half-mixed
        // with the last paragraph of the procedure.
        yield return new XElement(w + "p",
            new XElement(w + "r", new XElement(w + "br", new XAttribute(w + "type", "page"))));

        yield return Heading(w, "Electronic Signature Manifest");

        yield return Caption(w,
            $"{document.DocumentNumber}  ·  Revision {document.Revision:00}  ·  {document.Title}");

        yield return Caption(w,
            "The signatures below were applied electronically within the DMS. Each is bound to "
            + "the hash of the exact document content approved, shown at the foot of this page.");

        yield return SignatureTable(w, signatures);

        yield return Caption(w, "");

        // The §11.70 binding, printed. Without it the manifest asserts who signed but not what
        // they signed — and a signature page that could be attached to any document is not
        // evidence of anything.
        var hash = document.ApprovedContentHash ?? "(not recorded)";
        yield return Caption(w, $"Approved content hash (SHA-256): {hash}");

        yield return Caption(w,
            "This page is generated from the signature records held in the DMS audit trail. "
            + "It is not a transcription and cannot be edited.");
    }

    private static XElement SignatureTable(XNamespace w, IReadOnlyList<ElectronicSignature> signatures)
    {
        var table = new XElement(w + "tbl",
            new XElement(w + "tblPr",
                new XElement(w + "tblW",
                    new XAttribute(w + "w", "5000"), new XAttribute(w + "type", "pct")),
                new XElement(w + "tblBorders",
                    Border(w, "top"), Border(w, "left"), Border(w, "bottom"),
                    Border(w, "right"), Border(w, "insideH"), Border(w, "insideV"))),
            new XElement(w + "tblGrid",
                new XElement(w + "gridCol", new XAttribute(w + "w", "2400")),
                new XElement(w + "gridCol", new XAttribute(w + "w", "2200")),
                new XElement(w + "gridCol", new XAttribute(w + "w", "1800")),
                new XElement(w + "gridCol", new XAttribute(w + "w", "2800"))),
            HeaderRow(w, "Name", "Role / Designation", "Meaning", "Signed (UTC)"));

        if (signatures.Count == 0)
        {
            table.Add(DataRow(w, "No signatures recorded", "", "", ""));
            return table;
        }

        foreach (var signature in signatures)
        {
            // Printed name, department and designation come from the signature record, not
            // from the user table — §11.50(a)(1) asks that the signature read as it did when
            // it was applied, and a later job title change must not silently rewrite history.
            table.Add(DataRow(
                w,
                $"{signature.FullName} ({signature.UserName})",
                $"{signature.Designation}, {signature.Department}",
                Humanise(signature.Meaning.ToString()),
                signature.SignedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss")));

            if (!string.IsNullOrWhiteSpace(signature.Reason))
            {
                table.Add(NoteRow(w, $"Reason: {signature.Reason}"));
            }
        }

        return table;
    }

    private static XElement Border(XNamespace w, string edge) =>
        new(w + edge,
            new XAttribute(w + "val", "single"),
            new XAttribute(w + "sz", "4"),
            new XAttribute(w + "color", "808080"));

    private static XElement HeaderRow(XNamespace w, params string[] headings) =>
        new(w + "tr",
            new XElement(w + "trPr", new XElement(w + "tblHeader")),
            headings.Select(h => new XElement(w + "tc",
                new XElement(w + "tcPr",
                    new XElement(w + "shd",
                        new XAttribute(w + "val", "clear"),
                        new XAttribute(w + "fill", "F2F5F7"))),
                new XElement(w + "p",
                    new XElement(w + "r",
                        new XElement(w + "rPr", new XElement(w + "b"),
                            new XElement(w + "sz", new XAttribute(w + "val", "18"))),
                        new XElement(w + "t", h))))));

    private static XElement DataRow(XNamespace w, params string[] cells) =>
        new(w + "tr",
            cells.Select(c => new XElement(w + "tc",
                new XElement(w + "p",
                    new XElement(w + "r",
                        new XElement(w + "rPr", new XElement(w + "sz", new XAttribute(w + "val", "18"))),
                        new XElement(w + "t",
                            new XAttribute(XNamespace.Xml + "space", "preserve"), c))))));

    /// <summary>A full-width note spanning the table, for a rejection reason.</summary>
    private static XElement NoteRow(XNamespace w, string text) =>
        new(w + "tr",
            new XElement(w + "tc",
                new XElement(w + "tcPr",
                    new XElement(w + "gridSpan", new XAttribute(w + "val", "4"))),
                new XElement(w + "p",
                    new XElement(w + "r",
                        new XElement(w + "rPr",
                            new XElement(w + "i"),
                            new XElement(w + "sz", new XAttribute(w + "val", "16")),
                            new XElement(w + "color", new XAttribute(w + "val", "55636E"))),
                        new XElement(w + "t",
                            new XAttribute(XNamespace.Xml + "space", "preserve"), text)))));

    private static XElement Heading(XNamespace w, string text) =>
        new(w + "p",
            new XElement(w + "pPr", new XElement(w + "spacing", new XAttribute(w + "after", "160"))),
            new XElement(w + "r",
                new XElement(w + "rPr",
                    new XElement(w + "b"),
                    new XElement(w + "sz", new XAttribute(w + "val", "28"))),
                new XElement(w + "t", text)));

    private static XElement Caption(XNamespace w, string text) =>
        new(w + "p",
            new XElement(w + "pPr", new XElement(w + "spacing", new XAttribute(w + "after", "120"))),
            new XElement(w + "r",
                new XElement(w + "rPr",
                    new XElement(w + "sz", new XAttribute(w + "val", "16")),
                    new XElement(w + "color", new XAttribute(w + "val", "55636E"))),
                new XElement(w + "t",
                    new XAttribute(XNamespace.Xml + "space", "preserve"), text)));

    /// <summary>"ReviewedAndApproved" reads badly on a printed page; "Reviewed and approved" doesn't.</summary>
    private static string Humanise(string enumName)
    {
        var spaced = System.Text.RegularExpressions.Regex.Replace(enumName, "([a-z0-9])([A-Z])", "$1 $2");
        return spaced.Length == 0 ? spaced : char.ToUpperInvariant(spaced[0]) + spaced[1..].ToLowerInvariant();
    }
}
