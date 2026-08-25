using Dms.Domain.Entities;
using Dms.Domain.Enums;
using Dms.Domain.Services;
using Xunit;

namespace Dms.Domain.Tests;

public class PolicyAndSigningTests
{
    private static readonly DateOnly Today = new(2026, 8, 25);

    [Fact]
    public void Review_due_date_runs_from_the_effective_date()
    {
        // The clock on "is this still correct" starts when people begin following the
        // document, not when it was drafted or approved.
        var policy = new ReviewPolicy(Guid.CreateVersion7(), null, 24, 60, "admin");

        Assert.Equal(new DateOnly(2028, 8, 25), policy.DueDateFrom(Today));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(241)]
    public void Review_interval_outside_the_permitted_range_is_rejected(int months)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ReviewPolicy(Guid.CreateVersion7(), null, months, 30, "admin"));
    }

    [Fact]
    public void Pre_intimation_longer_than_the_interval_is_rejected()
    {
        // A document that starts warning before its own review period began would be
        // permanently "coming due", which trains people to ignore the report.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ReviewPolicy(Guid.CreateVersion7(), null, 1, 400, "admin"));
    }

    [Fact]
    public void Retention_expiry_runs_from_the_triggering_event()
    {
        var policy = new RetentionPolicy(Guid.CreateVersion7(), null, 10, RetentionTrigger.Obsolete, "admin");

        Assert.Equal(new DateOnly(2036, 8, 25), policy.RetainUntil(Today));
    }

    [Fact]
    public void Zero_year_retention_is_rejected()
    {
        // Would make a record disposable the moment it left use, which no schedule permits and
        // which is far more likely a typo than a decision.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RetentionPolicy(Guid.CreateVersion7(), null, 0, RetentionTrigger.Obsolete, "admin"));
    }

    [Fact]
    public void Site_specific_policy_is_more_specific_than_the_default()
    {
        var siteWide = new RetentionPolicy(Guid.CreateVersion7(), null, 5, RetentionTrigger.Obsolete, "admin");
        var siteOverride = new RetentionPolicy(
            Guid.CreateVersion7(), Guid.CreateVersion7(), 10, RetentionTrigger.Obsolete, "admin");

        Assert.True(siteOverride.Specificity > siteWide.Specificity);
    }

    [Fact]
    public void Password_verifies_against_its_own_hash_and_nothing_else()
    {
        var user = new DmsUser("a.nair", "A Nair", "QA", "Executive", "CorrectHorse#2026");

        Assert.True(user.VerifyPassword("CorrectHorse#2026"));
        Assert.False(user.VerifyPassword("correcthorse#2026"));
        Assert.False(user.VerifyPassword("wrong"));
    }

    [Fact]
    public void Login_lockout_and_signing_lockout_are_independent()
    {
        // §11.200 keeps the login credential distinct from the signing credential. Someone
        // brute-forcing a password from outside must not be able to stop a signer already at
        // their desk from completing an approval.
        var user = new DmsUser("a.nair", "A Nair", "QA", "Executive", "CorrectHorse#2026");
        var now = DateTimeOffset.UtcNow;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            user.RegisterFailedLogin(5, TimeSpan.FromMinutes(15), now);
        }

        Assert.True(user.IsLoginLockedOut(now));
        Assert.False(user.CanLogIn(now));

        Assert.False(user.IsLockedOut(now));
        Assert.True(user.CanSign(now));
    }

    [Fact]
    public void A_successful_login_clears_the_failure_count()
    {
        var user = new DmsUser("a.nair", "A Nair", "QA", "Executive", "CorrectHorse#2026");
        var now = DateTimeOffset.UtcNow;

        user.RegisterFailedLogin(5, TimeSpan.FromMinutes(15), now);
        user.RegisterSuccessfulLogin(now);

        Assert.Equal(0, user.FailedLoginAttempts);
        Assert.True(user.CanLogIn(now));
        Assert.Equal(now, user.LastLoginAt);
    }

    [Fact]
    public void An_uncontrolled_copy_carries_no_copy_number()
    {
        // Printing one would imply it is tracked. The disclaimer leads instead, because the
        // whole risk is someone finding it later and working from it.
        var watermark = ControlledCopyWatermark.Compose(
            CopyType.Uncontrolled, "MNK-QA-SOP-0001", 0, 3, 1, "R Sharma", DateTimeOffset.UtcNow);

        Assert.Contains("UNCONTROLLED", watermark, StringComparison.Ordinal);
        Assert.DoesNotContain("COPY 3", watermark, StringComparison.Ordinal);
    }

    [Fact]
    public void A_controlled_copy_carries_number_revision_and_print_sequence()
    {
        var watermark = ControlledCopyWatermark.Compose(
            CopyType.Controlled, "MNK-QA-SOP-0001", 2, 3, 4, "QA", DateTimeOffset.UtcNow);

        Assert.Contains("CONTROLLED COPY 3", watermark, StringComparison.Ordinal);
        Assert.Contains("Rev 02", watermark, StringComparison.Ordinal);
        Assert.Contains("Print 4", watermark, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_code_has_no_spaces_so_it_encodes_cleanly()
    {
        var code = ControlledCopyWatermark.ComposeScanCode("MNK-QA-SOP-0001", 2, 3);

        Assert.DoesNotContain(' ', code);
        Assert.Equal("MNK-QA-SOP-0001/R02/C0003", code);
    }

    [Fact]
    public void A_controlled_copy_stops_printing_at_its_limit()
    {
        var copy = new DocumentDistribution(
            Guid.CreateVersion7(), 1, CopyType.Controlled, null, "QA", "admin", printLimit: 2);

        copy.RecordPrint();
        copy.RecordPrint();

        Assert.False(copy.CanPrint);
        Assert.Throws<InvalidOperationException>(() => copy.RecordPrint());
    }

    [Fact]
    public void A_retrieved_copy_cannot_be_reprinted()
    {
        var copy = new DocumentDistribution(
            Guid.CreateVersion7(), 1, CopyType.Controlled, null, "QA", "admin", printLimit: 5);

        copy.Retrieve("admin");

        Assert.False(copy.IsOutstanding);
        Assert.Throws<InvalidOperationException>(() => copy.RecordPrint());
    }

    [Fact]
    public void Closing_a_copy_out_as_lost_requires_a_note()
    {
        var copy = new DocumentDistribution(
            Guid.CreateVersion7(), 1, CopyType.Controlled, null, "QA", "admin", printLimit: 5);

        Assert.Throws<ArgumentException>(() =>
            copy.CloseOut(DistributionStatus.Lost, "  ", "admin"));
    }

    [Fact]
    public void Retrieve_is_not_a_valid_close_out_outcome()
    {
        var copy = new DocumentDistribution(
            Guid.CreateVersion7(), 1, CopyType.Controlled, null, "QA", "admin", printLimit: 5);

        Assert.Throws<ArgumentException>(() =>
            copy.CloseOut(DistributionStatus.Retrieved, "Came back.", "admin"));
    }

    [Fact]
    public void An_expired_editing_session_can_be_taken_over()
    {
        var session = new EditingSession(
            Guid.CreateVersion7(), "a.nair", "key1", DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.True(session.HasExpired(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_closed_editing_session_accepts_no_further_saves()
    {
        var session = new EditingSession(
            Guid.CreateVersion7(), "a.nair", "key1", DateTimeOffset.UtcNow.AddHours(1));

        session.Close(EditingSessionStatus.CheckedIn, "a.nair");

        Assert.Throws<InvalidOperationException>(() => session.RecordSave(DateTimeOffset.UtcNow));
    }
}
