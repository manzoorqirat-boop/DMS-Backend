using System.Text;

namespace Dms.Domain.Services;

/// <summary>
/// Renders a notification subject or body from a configurable template such as
/// <c>"{DocumentNumber} Rev {Revision} is due for review on {DueDate}"</c>.
/// <para>
/// Same shape as <see cref="DocumentNumberPattern"/>, deliberately: one token syntax across
/// the product, validated when an administrator saves it rather than when the job runs at
/// 2am. A bad template discovered during a reminder sweep is a silent gap in the reminders;
/// the same fault caught at save time is a form error.
/// </para>
/// <para>
/// Substitution only — no conditionals, no loops, no expressions. A template language with
/// control flow inside a regulated system is a small unvalidated program, and the point of
/// configuration is to avoid needing one.
/// </para>
/// </summary>
public static class MessageTemplate
{
    /// <summary>
    /// Every token a rule may reference. Not all are meaningful for every notification kind —
    /// <see cref="Validate"/> takes the set available for the kind in question, so a rule can't
    /// reference a copy number on a review reminder and render a blank.
    /// </summary>
    public static class Tokens
    {
        public const string DocumentNumber = "DocumentNumber";
        public const string Title = "Title";
        public const string Revision = "Revision";
        public const string Status = "Status";
        public const string Department = "Department";
        public const string Site = "Site";
        public const string DueDate = "DueDate";
        public const string DaysUntilDue = "DaysUntilDue";
        public const string DaysOverdue = "DaysOverdue";
        public const string Recipient = "Recipient";
        public const string RecipientFullName = "RecipientFullName";
        public const string StepLabel = "StepLabel";
        public const string StepOrder = "StepOrder";
        public const string CopyNumber = "CopyNumber";
        public const string CopyType = "CopyType";
        public const string IssuedTo = "IssuedTo";
        public const string IssuedOn = "IssuedOn";
        public const string RetainUntil = "RetainUntil";
    }

    public static MessageTemplateValidation Validate(string template, IReadOnlyCollection<string> availableTokens)
    {
        var issues = new List<string>();

        if (string.IsNullOrWhiteSpace(template))
        {
            return new MessageTemplateValidation(false, ["Template text is required."]);
        }

        foreach (var token in ExtractTokens(template, issues))
        {
            if (!availableTokens.Contains(token))
            {
                issues.Add(
                    $"Token '{{{token}}}' isn't available here. Available: {string.Join(", ", availableTokens)}.");
            }
        }

        return new MessageTemplateValidation(issues.Count == 0, issues);
    }

    /// <summary>
    /// Substitutes values into a template. An unknown token renders as an empty string rather
    /// than throwing — by the time a sweep is running, a reminder with one blank field is far
    /// more useful than no reminder at all. Validation at save time is what stops it happening.
    /// </summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        var result = new StringBuilder();
        var index = 0;

        while (index < template.Length)
        {
            if (template[index] != '{')
            {
                result.Append(template[index]);
                index++;
                continue;
            }

            var close = template.IndexOf('}', index);
            if (close < 0)
            {
                result.Append(template[index..]);
                break;
            }

            var token = template[(index + 1)..close];
            result.Append(values.TryGetValue(token, out var value) ? value : "");
            index = close + 1;
        }

        return result.ToString();
    }

    private static List<string> ExtractTokens(string template, List<string> issues)
    {
        var tokens = new List<string>();
        var index = 0;

        while (index < template.Length)
        {
            var open = template.IndexOf('{', index);
            if (open < 0)
            {
                break;
            }

            var close = template.IndexOf('}', open);
            if (close < 0)
            {
                issues.Add($"Unterminated token starting at position {open}.");
                break;
            }

            tokens.Add(template[(open + 1)..close]);
            index = close + 1;
        }

        return tokens;
    }
}

public sealed record MessageTemplateValidation(bool IsValid, IReadOnlyList<string> Issues);
