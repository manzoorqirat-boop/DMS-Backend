using Dms.Domain.Services;
using Xunit;

namespace Dms.Domain.Tests;

public class DocumentNumberPatternTests
{
    private static NumberTokens Tokens(int sequence = 1, int revision = 0) =>
        new("MNK", "QA", "SOP", sequence, revision, new DateOnly(2026, 3, 9));

    [Fact]
    public void Default_pattern_renders_the_expected_shape()
    {
        var rendered = DocumentNumberPattern.Render(DocumentNumberPattern.Default, Tokens());

        Assert.Equal("MNK-QA-SOP-0001", rendered);
    }

    [Fact]
    public void Custom_pattern_with_year_renders_and_pads_independently()
    {
        var rendered = DocumentNumberPattern.Render("SOP/{DEPT}/{YYYY}/{SEQ:000}", Tokens(sequence: 42));

        Assert.Equal("SOP/QA/2026/042", rendered);
    }

    [Fact]
    public void Sequence_beyond_its_padding_renders_in_full_rather_than_truncating()
    {
        // A longer number is ugly; a truncated one collides with an existing document.
        var rendered = DocumentNumberPattern.Render("{TYPE}-{SEQ:000}", Tokens(sequence: 12345));

        Assert.Equal("SOP-12345", rendered);
    }

    [Fact]
    public void Revision_token_renders_padded()
    {
        var rendered = DocumentNumberPattern.Render("{TYPE}-{SEQ:0000}-R{REV:00}", Tokens(revision: 3));

        Assert.Equal("SOP-0001-R03", rendered);
    }

    [Fact]
    public void Pattern_without_a_sequence_token_is_rejected()
    {
        var result = DocumentNumberPattern.Validate("{SITE}-{DEPT}-{TYPE}");

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Contains("SEQ", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_token_is_rejected_at_configuration_time()
    {
        // Caught when an administrator saves the rule, not when an author creates a document.
        var result = DocumentNumberPattern.Validate("{SITE}-{PRODUCT}-{SEQ:0000}");

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Contains("PRODUCT", StringComparison.Ordinal));
    }

    [Fact]
    public void Unterminated_token_is_rejected()
    {
        var result = DocumentNumberPattern.Validate("{SITE-{SEQ:0000}");

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("{SITE}-{SEQ:0000}", "")]
    [InlineData("{YYYY}/{SEQ:000}", "2026")]
    [InlineData("{YYYY}{MM}/{SEQ:000}", "2026-03")]
    [InlineData("{MM}-{SEQ:000}", "03")]
    public void Period_key_reflects_whichever_date_tokens_the_pattern_uses(string pattern, string expected)
    {
        // The period key is what scopes the counter, so a pattern containing a year restarts
        // numbering each year without any code change.
        var key = DocumentNumberPattern.PeriodKeyFor(pattern, new DateOnly(2026, 3, 9));

        Assert.Equal(expected, key);
    }

    [Fact]
    public void Codes_are_upper_cased_regardless_of_input()
    {
        var rendered = DocumentNumberPattern.Render(
            DocumentNumberPattern.Default,
            new NumberTokens("mnk", "qa", "sop", 1, 0, new DateOnly(2026, 3, 9)));

        Assert.Equal("MNK-QA-SOP-0001", rendered);
    }

    [Fact]
    public void Zero_or_negative_sequence_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DocumentNumberFormat.Compose("MNK", "QA", "SOP", 0));
    }
}
