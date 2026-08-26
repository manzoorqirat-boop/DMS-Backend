using Dms.Domain.Entities;
using Dms.Domain.Enums;
using Dms.Domain.Services;
using Xunit;
using Dms.Domain.Common;

namespace Dms.Domain.Tests;

public class NotificationRuleTests
{
    private static NotificationRule Rule(
        NotificationKind kind = NotificationKind.ReviewComingDue,
        int repeatEveryDays = 0,
        string subject = "Review due: {DocumentNumber}",
        string body = "{Title} is due on {DueDate}.") =>
        new(kind, null, NotificationRecipientMode.DocumentAuthor, null, 30, repeatEveryDays,
            subject, body, "admin");

    [Fact]
    public void Templates_render_the_configured_wording()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DocumentNumber"] = "MNK-QA-SOP-0001",
            ["Title"] = "Vessel Cleaning",
            ["DueDate"] = "2026-11-30",
        };

        Assert.Equal("Review due: MNK-QA-SOP-0001", MessageTemplate.Render(Rule().SubjectTemplate, values));
        Assert.Equal("Vessel Cleaning is due on 2026-11-30.", MessageTemplate.Render(Rule().BodyTemplate, values));
    }

    [Fact]
    public void A_token_the_kind_does_not_offer_is_rejected_at_save_time()
    {
        // A copy number on a review reminder would validate at save and render blank months
        // later — exactly the error configuration is supposed to eliminate.
        Assert.Throws<ArgumentException>(() =>
            Rule(subject: "Copy {CopyNumber} of {DocumentNumber}"));
    }

    [Fact]
    public void A_token_valid_for_a_different_kind_is_accepted_there()
    {
        var rule = new NotificationRule(
            NotificationKind.CopyUnacknowledged, null, NotificationRecipientMode.CopyIssuer, null,
            7, 7, "Copy {CopyNumber} unacknowledged", "Issued to {IssuedTo} on {IssuedOn}.", "admin");

        Assert.Contains("{CopyNumber}", rule.SubjectTemplate, StringComparison.Ordinal);
    }

    [Fact]
    public void Notifying_role_holders_requires_a_role()
    {
        // Otherwise the rule would resolve to nobody, which is the worst possible failure for
        // a reminder — it looks configured and silently notifies no one.
        Assert.Throws<ArgumentException>(() => new NotificationRule(
            NotificationKind.ReviewOverdue, null, NotificationRecipientMode.RoleHolders, null,
            0, 1, "Overdue: {DocumentNumber}", "{Title} is overdue.", "admin"));
    }

    [Fact]
    public void A_role_is_cleared_when_the_recipient_mode_no_longer_needs_one()
    {
        var rule = new NotificationRule(
            NotificationKind.ReviewOverdue, null, NotificationRecipientMode.RoleHolders,
            Uuid7.NewGuid(), 0, 1, "Overdue: {DocumentNumber}", "{Title} is overdue.", "admin");

        rule.Update(NotificationRecipientMode.DocumentAuthor, null, 0, 1,
            "Overdue: {DocumentNumber}", "{Title} is overdue.");

        Assert.Null(rule.RecipientRoleId);
    }

    [Fact]
    public void A_send_once_rule_produces_a_stable_period_key()
    {
        var rule = Rule(repeatEveryDays: 0);

        Assert.Equal(
            rule.PeriodKeyFor(new DateOnly(2026, 1, 1)),
            rule.PeriodKeyFor(new DateOnly(2026, 6, 1)));
    }

    [Fact]
    public void A_daily_rule_produces_a_different_key_each_day()
    {
        var rule = Rule(kind: NotificationKind.ReviewOverdue, repeatEveryDays: 1,
            subject: "Overdue: {DocumentNumber}", body: "{Title} overdue by {DaysOverdue} days.");

        Assert.NotEqual(
            rule.PeriodKeyFor(new DateOnly(2026, 1, 1)),
            rule.PeriodKeyFor(new DateOnly(2026, 1, 2)));
    }

    [Fact]
    public void A_weekly_rule_holds_the_same_key_within_its_window()
    {
        var rule = Rule(kind: NotificationKind.ReviewOverdue, repeatEveryDays: 7,
            subject: "Overdue: {DocumentNumber}", body: "{Title} overdue.");

        var first = rule.PeriodKeyFor(new DateOnly(2026, 1, 1));

        Assert.Equal(first, rule.PeriodKeyFor(new DateOnly(2026, 1, 3)));
        Assert.NotEqual(first, rule.PeriodKeyFor(new DateOnly(2026, 1, 15)));
    }

    [Fact]
    public void Disabling_keeps_the_rule_rather_than_deleting_it()
    {
        // A deleted rule looks identical to one never configured, and "why did nobody get
        // warned" is easier to answer when the rule is still there marked off.
        var rule = Rule();
        rule.SetEnabled(false);

        Assert.False(rule.IsEnabled);
        Assert.NotEqual("", rule.SubjectTemplate);
    }

    [Fact]
    public void A_type_specific_rule_outranks_the_catch_all()
    {
        var catchAll = Rule();
        var typeSpecific = new NotificationRule(
            NotificationKind.ReviewComingDue, Uuid7.NewGuid(),
            NotificationRecipientMode.DocumentAuthor, null, 60, 0,
            "Review due: {DocumentNumber}", "{Title} is due on {DueDate}.", "admin");

        Assert.True(typeSpecific.Specificity > catchAll.Specificity);
    }

    [Fact]
    public void Negative_lead_or_repeat_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NotificationRule(
            NotificationKind.ReviewComingDue, null, NotificationRecipientMode.DocumentAuthor, null,
            -1, 0, "S {DocumentNumber}", "B {Title}", "admin"));
    }

    [Fact]
    public void An_unknown_token_renders_blank_rather_than_throwing_at_run_time()
    {
        // By the time a sweep is running, a reminder with one blank field is far more useful
        // than an exception that costs the whole batch.
        var rendered = MessageTemplate.Render(
            "{DocumentNumber} / {NotARealToken}",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["DocumentNumber"] = "SOP-1" });

        Assert.Equal("SOP-1 / ", rendered);
    }

    [Fact]
    public void An_unterminated_token_is_rejected_at_validation()
    {
        var result = MessageTemplate.Validate("{DocumentNumber", ["DocumentNumber"]);

        Assert.False(result.IsValid);
    }
}
