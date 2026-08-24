using System.Globalization;

namespace Dms.Domain.Services;

/// <summary>
/// Composes a document number from its four segments: site, department, type, sequence —
/// e.g. <c>MNK-QA-SOP-0001</c>.
/// <para>
/// Pure and I/O-free, like <see cref="DocxTemplateValidator"/>, so the format is unit-testable
/// on its own and there is exactly one place in the codebase that decides what a document
/// number looks like. Anything that needs to parse or display a number goes through here
/// rather than re-implementing the string concatenation.
/// </para>
/// <para>
/// Padding is fixed at four digits. That's a real ceiling of 9,999 documents per
/// site/department/type before numbers grow a fifth digit and stop sorting lexically — which
/// is a decision worth revisiting if any single department is expected to approach it, but
/// changing it later would make old and new numbers inconsistent, so it is pinned here
/// rather than left to a config value someone can quietly change mid-life.
/// </para>
/// </summary>
public static class DocumentNumberFormat
{
    public const char Separator = '-';

    public const int SequenceDigits = 4;

    public static string Compose(string siteCode, string departmentCode, string typeCode, int sequence)
    {
        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "Sequence must be positive.");
        }

        var padded = sequence.ToString(CultureInfo.InvariantCulture).PadLeft(SequenceDigits, '0');

        return string.Join(
            Separator,
            Require(siteCode, nameof(siteCode)),
            Require(departmentCode, nameof(departmentCode)),
            Require(typeCode, nameof(typeCode)),
            padded);
    }

    /// <summary>
    /// Revision label as it appears on the document face. Revision 0 is the first issue, and
    /// prints as <c>00</c> rather than being blank — an unlabelled revision is ambiguous on a
    /// printed controlled copy.
    /// </summary>
    public static string ComposeRevision(int revision) =>
        revision >= 0
            ? revision.ToString(CultureInfo.InvariantCulture).PadLeft(2, '0')
            : throw new ArgumentOutOfRangeException(nameof(revision), "Revision cannot be negative.");

    private static string Require(string value, string paramName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{paramName} is required.", paramName)
            : value.Trim().ToUpperInvariant();
}
