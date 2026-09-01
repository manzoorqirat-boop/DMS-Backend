using Dms.Domain.Constants;
using Dms.Domain.Services;
using Xunit;

namespace Dms.Domain.Tests;

/// <summary>
/// <c>DocxMetadataWriter</c> stamps values in; <c>DocxProtectionVerifier</c> later checks they
/// are unchanged. Nothing else in the system verifies that those two agree — and if they
/// disagree by so much as a trimmed space, every save of an untouched document is rejected as
/// tampering. These are the tests the integrity story actually rests on.
/// </summary>
public class DocxMetadataRoundTripTests
{
    private static Dictionary<string, string> SampleMetadata() => new(StringComparer.Ordinal)
    {
        [TemplateFieldTags.DocumentNumber] = "MNK-QA-SOP-0001",
        [TemplateFieldTags.Title] = "Cleaning of Vessel V-101",
        [TemplateFieldTags.Revision] = "00",
        [TemplateFieldTags.EffectiveDate] = "",
        [TemplateFieldTags.Department] = "Quality Assurance",
        [TemplateFieldTags.Author] = "a.nair",
        [TemplateFieldTags.CreatedDate] = "2026-08-25",
    };

    [Fact]
    public void Values_are_written_into_page_header_controls()
    {
        // Header controls were previously left permanently blank: the writer only rewrote
        // word/document.xml, so a document number placed in the header — where it belongs, so
        // it repeats per page — never got filled in.
        var docx = TestDocx.Build(tags: [], headerTags: [TemplateFieldTags.DocumentNumber]);

        var result = DocxMetadataWriter.Write(
            docx,
            new Dictionary<string, string> { [TemplateFieldTags.DocumentNumber] = "ND-QIC-SOP-0042" });

        Assert.Empty(result.MissingTags);

        var verification = DocxProtectionVerifier.Verify(
            result.Content,
            new Dictionary<string, string> { [TemplateFieldTags.DocumentNumber] = "ND-QIC-SOP-0042" });

        // Verified through the verifier rather than by re-reading the zip: that proves both
        // halves learned about headers, and that they agree.
        Assert.DoesNotContain(verification.Findings, f => f.Contains("DocNo", StringComparison.Ordinal));
    }

    [Fact]
    public void Written_document_passes_verification_with_the_same_values()
    {
        var metadata = SampleMetadata();

        var result = DocxMetadataWriter.Write(TestDocx.ValidTemplate(), metadata);
        Assert.Empty(result.MissingTags);

        var verification = DocxProtectionVerifier.Verify(result.Content, metadata);

        Assert.True(
            verification.IsValid,
            $"Round-trip failed: {string.Join(" | ", verification.Findings)}");
    }

    [Fact]
    public void Round_trip_survives_placeholders_split_across_runs()
    {
        // Word routinely splits a single logical value across several runs after an edit or a
        // spellcheck. If the writer filled only the first run, or the verifier read only the
        // first, an untouched document would be reported as altered.
        var metadata = SampleMetadata();

        var result = DocxMetadataWriter.Write(TestDocx.ValidTemplate(splitRuns: true), metadata);
        var verification = DocxProtectionVerifier.Verify(result.Content, metadata);

        Assert.True(
            verification.IsValid,
            $"Split-run round-trip failed: {string.Join(" | ", verification.Findings)}");
    }

    [Fact]
    public void Empty_value_round_trips()
    {
        // EffectiveDate is deliberately blank on a draft. An empty string has to survive as an
        // empty string rather than collapsing to the original placeholder text.
        var metadata = SampleMetadata();

        var result = DocxMetadataWriter.Write(TestDocx.ValidTemplate(), metadata);

        Assert.Equal("", TestDocx.ReadTag(result.Content, TemplateFieldTags.EffectiveDate));
    }

    [Fact]
    public void Writer_replaces_placeholder_text_with_the_supplied_value()
    {
        var metadata = SampleMetadata();

        var result = DocxMetadataWriter.Write(TestDocx.ValidTemplate(), metadata);

        Assert.Equal("MNK-QA-SOP-0001", TestDocx.ReadTag(result.Content, TemplateFieldTags.DocumentNumber));
        Assert.Equal("Quality Assurance", TestDocx.ReadTag(result.Content, TemplateFieldTags.Department));
    }

    [Fact]
    public void Writer_reports_tags_the_template_does_not_declare()
    {
        var template = TestDocx.Build([TemplateFieldTags.Title]);

        var result = DocxMetadataWriter.Write(template, SampleMetadata());

        Assert.Contains(TemplateFieldTags.DocumentNumber, result.MissingTags);
        Assert.DoesNotContain(TemplateFieldTags.Title, result.MissingTags);
    }

    [Fact]
    public void Repackaging_preserves_parts_the_writer_does_not_touch()
    {
        // Styles, numbering, fonts, headers and the protection settings all have to survive
        // untouched. Round-tripping them through an XML parser risks changes Word notices.
        var result = DocxMetadataWriter.Write(TestDocx.ValidTemplate(), SampleMetadata());

        Assert.True(TestDocx.HasPart(result.Content, "word/styles.xml"));
        Assert.True(TestDocx.HasPart(result.Content, "word/settings.xml"));
    }

    [Fact]
    public void Verification_fails_when_a_metadata_value_was_altered()
    {
        var metadata = SampleMetadata();
        var written = DocxMetadataWriter.Write(TestDocx.ValidTemplate(), metadata).Content;

        // Simulate an author who defeated the lock and edited the document number.
        var tampered = DocxMetadataWriter.Write(
            written,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [TemplateFieldTags.DocumentNumber] = "MNK-QA-SOP-9999",
            }).Content;

        var verification = DocxProtectionVerifier.Verify(tampered, metadata);

        Assert.False(verification.IsValid);
        Assert.Contains(verification.Findings, f => f.Contains(TemplateFieldTags.DocumentNumber, StringComparison.Ordinal));
    }

    [Fact]
    public void Verification_finding_does_not_echo_the_altered_value()
    {
        // Findings land in the audit trail. Copying attacker-controlled text verbatim into a
        // regulated record is a poor idea, so the finding says what changed, not to what.
        var metadata = SampleMetadata();
        var written = DocxMetadataWriter.Write(TestDocx.ValidTemplate(), metadata).Content;

        var tampered = DocxMetadataWriter.Write(
            written,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [TemplateFieldTags.Title] = "INJECTED-SENTINEL-VALUE",
            }).Content;

        var verification = DocxProtectionVerifier.Verify(tampered, metadata);

        Assert.False(verification.IsValid);
        Assert.DoesNotContain(
            verification.Findings,
            f => f.Contains("INJECTED-SENTINEL-VALUE", StringComparison.Ordinal));
    }

    [Fact]
    public void Verification_fails_when_protection_was_switched_off()
    {
        var metadata = SampleMetadata();

        var unprotected = TestDocx.Build(TemplateFieldTags.Required, enforceProtection: false);
        var written = DocxMetadataWriter.Write(unprotected, metadata).Content;

        var verification = DocxProtectionVerifier.Verify(written, metadata);

        Assert.False(verification.IsValid);
        Assert.Contains(verification.Findings, f => f.Contains("protection", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Verification_fails_when_a_content_control_was_deleted()
    {
        var metadata = SampleMetadata();

        // Template declares only Title, so every other expected control is absent.
        var stripped = TestDocx.Build([TemplateFieldTags.Title]);
        var written = DocxMetadataWriter.Write(stripped, metadata).Content;

        var verification = DocxProtectionVerifier.Verify(written, metadata);

        Assert.False(verification.IsValid);
        Assert.Contains(verification.Findings, f => f.Contains("missing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Verification_rejects_a_file_that_is_not_a_docx()
    {
        var verification = DocxProtectionVerifier.Verify("not a zip"u8.ToArray(), SampleMetadata());

        Assert.False(verification.IsValid);
        Assert.NotEmpty(verification.Findings);
    }
}
