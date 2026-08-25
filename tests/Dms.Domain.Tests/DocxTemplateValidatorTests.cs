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
