using System.Globalization;
using Dms.Domain.Enums;

namespace Dms.Domain.Services;

/// <summary>
/// Composes the text stamped across a printed copy.
/// <para>
/// Pure and I/O-free, like the other docx services, so the exact wording is unit-testable and
/// there is one definition of it. The composed string is stored on the
/// <c>PrintEvent</c> rather than recomputed at display time — a page recovered from a filing
/// cabinet years later has to reconcile against what the system says was printed that day,
/// even if the format has changed since.
/// </para>
/// </summary>
public static class ControlledCopyWatermark
{
    /// <param name="printSequence">1 for the first print of this copy, 2 for a reprint.</param>
    public static string Compose(
        CopyType copyType,
        string documentNumber,
        int revision,
        int copyNumber,
        int printSequence,
        string issuedToName,
        DateTimeOffset printedAt)
    {
        var stamp = printedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        var rev = DocumentNumberFormat.ComposeRevision(revision);

        return copyType switch
        {
            // No copy number: an uncontrolled copy isn't tracked, and printing a number on it
            // would imply it is. The disclaimer leads, because the whole risk with this kind of
            // copy is someone finding it later and working from it.
            CopyType.Uncontrolled =>
                $"UNCONTROLLED COPY — VERIFY CURRENT VERSION BEFORE USE | {documentNumber} Rev {rev} | "
                + $"Printed {stamp} by request of {issuedToName}",

            CopyType.External =>
                $"EXTERNAL COPY {copyNumber} | {documentNumber} Rev {rev} | Issued to {issuedToName} | "
                + $"Print {printSequence} | {stamp}",

            _ =>
                $"CONTROLLED COPY {copyNumber} | {documentNumber} Rev {rev} | Issued to {issuedToName} | "
                + $"Print {printSequence} | {stamp}",
        };
    }

    /// <summary>
    /// Short code for a barcode or QR label, letting a physical copy be scanned back against
    /// the register at retrieval — the reconciliation pattern used across this product
    /// category. Deliberately compact and free of spaces so it encodes cleanly.
    /// </summary>
    public static string ComposeScanCode(string documentNumber, int revision, int copyNumber) =>
        $"{documentNumber}/R{DocumentNumberFormat.ComposeRevision(revision)}/C{copyNumber:0000}";
}
