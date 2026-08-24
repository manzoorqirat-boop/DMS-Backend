using System.Globalization;
using System.Text;

namespace Dms.Domain.Services;

/// <summary>
/// Renders a document number from a configurable pattern such as
/// <c>{SITE}-{DEPT}-{TYPE}-{SEQ:0000}</c> or <c>SOP/{DEPT}/{YYYY}/{SEQ:000}</c>.
/// <para>
/// This replaces the fixed four-segment format the build started with. Numbering convention is
/// exactly the kind of thing that differs between companies, between sites within a company,
/// and sometimes between document types at one site — hardcoding it guarantees the first
/// customer whose SOPs read <c>SOP/QA/2026/001</c> needs a code change to onboard.
/// </para>
/// <para>
/// What is <i>not</i> configurable: that a number is issued exactly once, from a gap-free
/// counter, and never reissued. The pattern decides what the number looks like; it does not
/// decide whether numbering is controlled.
/// </para>
/// </summary>
public static class DocumentNumberPattern
{
    /// <summary>Used when a document type has no numbering rule configured.</summary>
    public const string Default = "{SITE}-{DEPT}-{TYPE}-{SEQ:0000}";

    public const string SiteToken = "SITE";
    public const string DepartmentToken = "DEPT";
    public const string TypeToken = "TYPE";
    public const string SequenceToken = "SEQ";
    public const string RevisionToken = "REV";
    public const string YearToken = "YYYY";
    public const string ShortYearToken = "YY";
    public const string MonthToken = "MM";

    private static readonly string[] KnownTokens =
    [
        SiteToken, DepartmentToken, TypeToken, SequenceToken,
        RevisionToken, YearToken, ShortYearToken, MonthToken,
    ];

    /// <summary>
    /// Checks a pattern before it is saved as master data.
    /// <para>
    /// Validated at configuration time rather than at document-creation time on purpose. A bad
    /// pattern discovered when an author tries to create an SOP is an outage in the middle of
    /// someone's work; the same fault caught when the administrator saves it is a form error.
    /// </para>
    /// </summary>
    public static PatternValidationResult Validate(string pattern)
    {
        var issues = new List<string>();

        if (string.IsNullOrWhiteSpace(pattern))
        {
            return new PatternValidationResult(false, ["Pattern is required."]);
        }

        var tokens = ExtractTokens(pattern, issues);

        if (!tokens.Contains(SequenceToken))
        {
            // Without a sequence, every document of a type would render the same number.
            issues.Add($"Pattern must contain a {{{SequenceToken}}} token, or numbers won't be unique.");
        }

        foreach (var unknown in tokens.Where(t => !KnownTokens.Contains(t, StringComparer.Ordinal)))
        {
            issues.Add($"Unknown token '{{{unknown}}}'. Known tokens: {string.Join(", ", KnownTokens)}.");
        }

        return new PatternValidationResult(issues.Count == 0, issues);
    }

    public static string Render(string pattern, NumberTokens tokens)
    {
        var result = new StringBuilder();
        var index = 0;

        while (index < pattern.Length)
        {
            if (pattern[index] != '{')
            {
                result.Append(pattern[index]);
                index++;
                continue;
            }

            var close = pattern.IndexOf('}', index);
            if (close < 0)
            {
                // Unterminated brace. Validate() rejects this at configuration time, so
                // reaching it here means a pattern was persisted without validation.
                throw new FormatException($"Unterminated token in pattern '{pattern}'.");
            }

            var body = pattern[(index + 1)..close];
            result.Append(RenderToken(body, tokens, pattern));
            index = close + 1;
        }

        return result.ToString();
    }

    /// <summary>
    /// The part of the pattern that determines when a counter restarts.
    /// <para>
    /// A pattern containing a year or month token implies the sequence resets for each period —
    /// <c>SOP/QA/2026/001</c> starting again at 001 in 2027 is the point of putting the year in
    /// the number. Returning that period as a key lets the counter be scoped to it. A pattern
    /// with no period token returns empty, meaning one continuous run forever.
    /// </para>
    /// </summary>
    public static string PeriodKeyFor(string pattern, DateOnly asOf)
    {
        var tokens = ExtractTokens(pattern, []);

        var hasYear = tokens.Contains(YearToken) || tokens.Contains(ShortYearToken);
        var hasMonth = tokens.Contains(MonthToken);

        return (hasYear, hasMonth) switch
        {
            (true, true) => asOf.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            (true, false) => asOf.ToString("yyyy", CultureInfo.InvariantCulture),
            (false, true) => asOf.ToString("MM", CultureInfo.InvariantCulture),
            _ => "",
        };
    }

    private static string RenderToken(string body, NumberTokens tokens, string pattern)
    {
        var separator = body.IndexOf(':');
        var name = separator < 0 ? body : body[..separator];
        var format = separator < 0 ? null : body[(separator + 1)..];

        return name switch
        {
            SiteToken => tokens.SiteCode,
            DepartmentToken => tokens.DepartmentCode,
            TypeToken => tokens.TypeCode,
            SequenceToken => Pad(tokens.Sequence, format ?? "0000"),
            RevisionToken => Pad(tokens.Revision, format ?? "00"),
            YearToken => tokens.AsOf.ToString("yyyy", CultureInfo.InvariantCulture),
            ShortYearToken => tokens.AsOf.ToString("yy", CultureInfo.InvariantCulture),
            MonthToken => tokens.AsOf.ToString("MM", CultureInfo.InvariantCulture),
            _ => throw new FormatException($"Unknown token '{name}' in pattern '{pattern}'."),
        };
    }

    /// <summary>
    /// Pads to the width implied by the format string. A number that outgrows its width is
    /// rendered in full rather than truncated — a longer number is ugly, a truncated one
    /// collides with an existing document.
    /// </summary>
    private static string Pad(int value, string format)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        return text.Length >= format.Length ? text : text.PadLeft(format.Length, '0');
    }

    private static List<string> ExtractTokens(string pattern, List<string> issues)
    {
        var tokens = new List<string>();
        var index = 0;

        while (index < pattern.Length)
        {
            var open = pattern.IndexOf('{', index);
            if (open < 0)
            {
                break;
            }

            var close = pattern.IndexOf('}', open);
            if (close < 0)
            {
                issues.Add($"Unterminated token starting at position {open}.");
                break;
            }

            var body = pattern[(open + 1)..close];
            var separator = body.IndexOf(':');
            tokens.Add(separator < 0 ? body : body[..separator]);
            index = close + 1;
        }

        return tokens;
    }
}

public sealed record NumberTokens(
    string SiteCode,
    string DepartmentCode,
    string TypeCode,
    int Sequence,
    int Revision,
    DateOnly AsOf);

public sealed record PatternValidationResult(bool IsValid, IReadOnlyList<string> Issues);
