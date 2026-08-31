using Dms.Domain.Entities;
using Dms.Domain.Services;
using Xunit;

namespace Dms.Domain.Tests;

public class PasswordPolicyTests
{
    private static PasswordPolicy Policy(
        int minLength = 8,
        int expiryDays = 90,
        int history = 3,
        bool complexity = true)
    {
        var policy = PasswordPolicy.CreateDefault("admin");
        policy.Update(minLength, expiryDays, history, 5, 15, complexity, "admin");
        return policy;
    }

    [Fact]
    public void A_new_account_must_change_its_password()
    {
        // The administrator who created the account knows this password, and here the password
        // is also the signing credential — so it cannot be allowed to sign anything until the
        // holder has made it theirs alone.
        var user = new DmsUser("a.nair", "A Nair", "QA", "Executive", "CorrectHorse#2026");

        Assert.True(user.MustChangePassword);
    }

    [Fact]
    public void Changing_the_password_clears_the_forced_change()
    {
        var user = new DmsUser("a.nair", "A Nair", "QA", "Executive", "CorrectHorse#2026");

        user.ChangePassword("DifferentHorse#2026");

        Assert.False(user.MustChangePassword);
    }

    [Fact]
    public void A_recently_used_password_cannot_be_reused()
    {
        var user = new DmsUser("a.nair", "A Nair", "QA", "Executive", "First#Pass2026");

        user.ChangePassword("Second#Pass2026", historyDepth: 3);
        user.ChangePassword("Third#Pass2026", historyDepth: 3);

        Assert.True(user.MatchesRecentPassword("First#Pass2026", 3));
        Assert.True(user.MatchesRecentPassword("Second#Pass2026", 3));
        Assert.False(user.MatchesRecentPassword("NeverUsed#2026", 3));
    }

    [Fact]
    public void History_is_trimmed_to_the_configured_depth()
    {
        // Keeping more old hashes than the policy will ever consult is retaining credential
        // material for no purpose, so the oldest drop off.
        var user = new DmsUser("a.nair", "A Nair", "QA", "Executive", "One#Pass2026");

        user.ChangePassword("Two#Pass2026", historyDepth: 2);
        user.ChangePassword("Three#Pass2026", historyDepth: 2);
        user.ChangePassword("Four#Pass2026", historyDepth: 2);

        Assert.False(user.MatchesRecentPassword("One#Pass2026", 2));
        Assert.True(user.MatchesRecentPassword("Three#Pass2026", 2));
    }

    [Theory]
    [InlineData("short1!", "at least")]
    [InlineData("alllowercase1!", "uppercase")]
    [InlineData("NoDigitsHere!", "number")]
    [InlineData("NoSymbols2026", "special")]
    public void Complexity_rules_are_enforced(string password, string expectedFragment)
    {
        var result = PasswordPolicyValidator.Validate(password, Policy());

        Assert.NotNull(result);
        Assert.Contains(expectedFragment, result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_compliant_password_passes()
    {
        Assert.Null(PasswordPolicyValidator.Validate("CorrectHorse#2026", Policy()));
    }

    [Fact]
    public void Complexity_can_be_switched_off_without_affecting_length()
    {
        var relaxed = Policy(complexity: false);

        Assert.Null(PasswordPolicyValidator.Validate("all lowercase passphrase", relaxed));
        Assert.NotNull(PasswordPolicyValidator.Validate("short", relaxed));
    }

    [Fact]
    public void The_length_floor_holds_even_when_the_policy_asks_for_less()
    {
        // ERES hit exactly this: a configured minimum below the floor was silently ignored
        // while the message still quoted the old number. Clamped on write and floored on read,
        // so the two can never disagree.
        var policy = Policy(minLength: 2);

        Assert.Equal(PasswordPolicyValidator.AbsoluteMinimumLength, policy.MinimumLength);
    }

    [Fact]
    public void Expiry_of_zero_means_never()
    {
        var never = Policy(expiryDays: 0);
        var longAgo = DateTimeOffset.UtcNow.AddYears(-5);

        Assert.False(PasswordPolicyValidator.HasExpired(longAgo, never, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_password_older_than_the_expiry_has_expired()
    {
        var policy = Policy(expiryDays: 90);
        var now = DateTimeOffset.UtcNow;

        Assert.True(PasswordPolicyValidator.HasExpired(now.AddDays(-91), policy, now));
        Assert.False(PasswordPolicyValidator.HasExpired(now.AddDays(-89), policy, now));
    }

    [Fact]
    public void Policy_values_are_clamped_rather_than_rejected()
    {
        var policy = PasswordPolicy.CreateDefault("admin");

        policy.Update(9999, -5, 999, 1, 99999, true, "admin");

        Assert.Equal(64, policy.MinimumLength);
        Assert.Equal(0, policy.ExpiryDays);
        Assert.Equal(24, policy.HistoryCount);
        Assert.Equal(3, policy.MaxFailedAttempts);
        Assert.Equal(1440, policy.LockoutMinutes);
    }
}
