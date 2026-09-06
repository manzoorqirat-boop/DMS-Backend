using Dms.Domain.Entities;

namespace Dms.Domain.Services;

/// <summary>
/// Composes the status stamp printed across every page of a rendered PDF.
/// <para>
/// Pure and I/O-free: the caller supplies the document and the configured stamps, and gets back
/// the text and colour to print, or null when that status is configured to print nothing.
/// </para>
/// <para>
/// <b>Rendered fresh on every render, never cached with the file.</b> That is the whole point,
/// and the deliberate difference from <see cref="ControlledCopyWatermark"/>: a controlled copy's
/// watermark is composed once and stored on the PrintEvent, because a page recovered from a
/// filing cabinet must reconcile against what the system says was printed that day. A status
/// stamp is the opposite — it must reflect what the document is <i>now</i>. A PDF rendered while
/// a document was Effective and read after it was superseded would otherwise still claim to be
/// current, which is the failure this stamp exists to prevent.
/// </para>
/// <para>
/// This is why <c>ApprovedPdfService</c> puts the status in its cache key: Effective and
/// Obsolete are different files, both correct, neither overwriting the other.
/// </para>
/// </summary>
public static class DocumentStatusStamp
{
    /// <summary>Text and colour to print, or null when nothing should be stamped.</summary>
    public sealed record Rendered(string Text, string ColourHex);

    /// <summary>
    /// Tokens available in stamp templates. A deliberately small set: a stamp is read at a
    /// glance on a printed page, so anything longer than a line defeats its purpose.
    /// </summary>
    public static IReadOnlyList<string> AvailableTokens { get; } =
    [
        "Status",
        "DocumentNumber",
        "Revision",
        "EffectiveDate",
        "Title",
    ];

    public static Rendered? Compose(ControlledDocument document, DocumentStatusStamps stamps)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(stamps);

        var stamp = stamps.For(document.Status);

        if (!stamp.IsEnabled || string.IsNullOrWhiteSpace(stamp.Text))
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Status"] = document.Status.ToString().ToUpperInvariant(),
            ["DocumentNumber"] = document.DocumentNumber,
            ["Revision"] = $"{document.Revision:00}",

            // An Approved document has no effective date until it is issued, and its default
            // stamp quotes one. "(not yet set)" rather than a blank keeps the sentence readable
            // — "effective  — DO NOT USE UNTIL THEN" reads like a rendering fault.
            ["EffectiveDate"] = document.EffectiveDate?.ToString("yyyy-MM-dd") ?? "(not yet set)",
            ["Title"] = document.Title,
        };

        var text = MessageTemplate.Render(stamp.Text, values).Trim();

        return string.IsNullOrWhiteSpace(text)
            ? null
            : new Rendered(text, Normalise(stamp.ColourHex));
    }

    /// <summary>
    /// Six hex digits, uppercase, no leading hash — the form WordprocessingML's w:color wants.
    /// Falls back to black on anything unrecognised rather than emitting invalid XML that would
    /// make the whole document unopenable over a mistyped colour.
    /// </summary>
    private static string Normalise(string? colourHex)
    {
        var trimmed = (colourHex ?? "").TrimStart('#').Trim();

        return trimmed.Length == 6 && trimmed.All(Uri.IsHexDigit)
            ? trimmed.ToUpperInvariant()
            : "000000";
    }
}
