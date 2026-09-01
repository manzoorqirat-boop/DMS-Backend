using Dms.Domain.Constants;
using Dms.Domain.Services;
using Xunit;

namespace Dms.Domain.Tests;

public class DocxTemplateValidatorTests
{
    [Fact]
    public void Well_formed_template_passes()
    {
        var result = DocxTemplateValidator.Validate(TestDocx.ValidTemplate());

        Assert.True(result.IsValid, string.Join(" | ", result.Issues));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Missing_content_control_is_reported_by_name()
    {
        var withoutAuthor = TemplateFieldTags.Required.Where(t => t != TemplateFieldTags.Author).ToList();

        var result = DocxTemplateValidator.Validate(TestDocx.Build(withoutAuthor));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Contains(TemplateFieldTags.Author, StringComparison.Ordinal));
    }

    [Fact]
    public void Protection_declared_but_not_enforced_fails()
    {
        // The "Restrict Editing opened and abandoned" case: the template looks locked in the
        // Word UI while every region stays editable once the file leaves Word.
        var result = DocxTemplateValidator.Validate(
            TestDocx.Build(TemplateFieldTags.Required, enforceProtection: false));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Contains("protection", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Missing_settings_part_fails_as_unprotected()
    {
        var result = DocxTemplateValidator.Validate(
            TestDocx.Build(TemplateFieldTags.Required, includeSettings: false));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Contains("protection", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Controls_in_a_page_header_satisfy_the_requirement()
    {
        // The arrangement a real controlled document uses: metadata in the header so it repeats
        // on every printed page. This previously failed validation for "missing" controls that
        // were present, because only word/document.xml was inspected.
        var result = DocxTemplateValidator.Validate(
            TestDocx.Build(
                tags: [],
                lockControls: true,
                headerTags: TemplateFieldTags.Required));

        Assert.True(result.IsValid, string.Join(" | ", result.Issues));
    }

    [Fact]
    public void Controls_split_between_header_and_body_are_both_found()
    {
        var half = TemplateFieldTags.Required.Take(3).ToList();
        var rest = TemplateFieldTags.Required.Skip(3).ToList();

        var result = DocxTemplateValidator.Validate(
            TestDocx.Build(tags: rest, lockControls: true, headerTags: half));

        Assert.True(result.IsValid, string.Join(" | ", result.Issues));
    }

    [Fact]
    public void Locked_content_controls_pass_without_document_protection()
    {
        // The OnlyOffice-authored case. Every metadata control is individually locked, so the
        // metadata can't be overwritten and the body needs no exception range carved out of a
        // document-wide lock. This is the shape that previously failed validation while being
        // the only shape that actually worked in the editor.
        var result = DocxTemplateValidator.Validate(
            TestDocx.Build(
                TemplateFieldTags.Required,
                enforceProtection: false,
                includeEditableRange: false,
                lockControls: true));

        Assert.True(result.IsValid, string.Join(" | ", result.Issues));
    }

    [Fact]
    public void Unlocked_controls_without_document_protection_fail()
    {
        var result = DocxTemplateValidator.Validate(
            TestDocx.Build(
                TemplateFieldTags.Required,
                enforceProtection: false,
                includeEditableRange: false,
                lockControls: false));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Contains("protected", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Failure_message_names_the_unlocked_controls()
    {
        // Naming what to fix beats saying only that something is wrong — whoever built the
        // template needs to know which control to go back to.
        var result = DocxTemplateValidator.Validate(
            TestDocx.Build(
                TemplateFieldTags.Required,
                enforceProtection: false,
                includeEditableRange: false,
                lockControls: false));

        Assert.Contains(result.Issues, i => i.Contains(TemplateFieldTags.DocumentNumber, StringComparison.Ordinal));
    }

    [Fact]
    public void Protected_document_with_no_editable_range_fails()
    {
        // Protecting everything with no exception leaves the author no body to write in.
        var result = DocxTemplateValidator.Validate(
            TestDocx.Build(TemplateFieldTags.Required, includeEditableRange: false));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Contains("editing range", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Non_docx_input_fails_without_throwing()
    {
        var result = DocxTemplateValidator.Validate("this is not a zip archive"u8.ToArray());

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Issues);
    }

    [Fact]
    public void Custom_tag_set_is_honoured_over_the_defaults()
    {
        // Metadata fields are configurable per document type, so a template carrying entirely
        // different tag names must validate against that type's configured set.
        var template = TestDocx.Build(["SOP_No", "SOP_Title"]);

        var result = DocxTemplateValidator.Validate(template, ["SOP_No", "SOP_Title"]);

        Assert.True(result.IsValid, string.Join(" | ", result.Issues));
    }

    [Fact]
    public void Empty_required_set_still_checks_protection()
    {
        // A type may require no metadata fields at all; that must not turn off the structural
        // checks that protect the document.
        var result = DocxTemplateValidator.Validate(
            TestDocx.Build([], enforceProtection: false), []);

        Assert.False(result.IsValid);
    }
}
