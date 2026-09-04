using Dms.Domain.Common;
using Dms.Domain.Entities;
using Dms.Domain.Enums;
using Xunit;

namespace Dms.Domain.Tests;

public class PendingActionTests
{
    private static PendingAction Pending(
        SecondSignatureTiming timing = SecondSignatureTiming.VerificationAfter) =>
        new(ControlledAction.CloseOutCopy, timing, "DocumentDistribution", Uuid7.NewGuid(),
            "ND-QIC-SOP-0004/R00/C0003", "{}", Permission.DocumentIssue,
            Uuid7.NewGuid(), Uuid7.NewGuid());

    private static ActionSignature Signature(
        PendingAction action, string userName, ActionSignatureMeaning meaning, string? reason = null) =>
        new(action.Id, Uuid7.NewGuid(), userName, $"{userName} Person", "QA", "Executive",
            meaning, reason);

    [Fact]
    public void One_person_cannot_supply_both_signatures()
    {
        // The whole point of a countersignature. Easy to get wrong precisely when it matters
        // most — the person holding every permission is the one who could bypass it.
        var action = Pending();
        action.AddSignature(Signature(action, "a.nair", ActionSignatureMeaning.Performed));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            action.AddSignature(Signature(action, "a.nair", ActionSignatureMeaning.Verified)));

        Assert.Contains("different person", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_same_person_is_matched_regardless_of_case()
    {
        var action = Pending();
        action.AddSignature(Signature(action, "a.nair", ActionSignatureMeaning.Performed));

        Assert.Throws<InvalidOperationException>(() =>
            action.AddSignature(Signature(action, "A.Nair", ActionSignatureMeaning.Verified)));
    }

    [Fact]
    public void Completion_requires_two_signatures()
    {
        var action = Pending();
        action.AddSignature(Signature(action, "a.nair", ActionSignatureMeaning.Performed));

        Assert.Throws<InvalidOperationException>(action.Complete);
    }

    [Fact]
    public void Two_different_people_can_complete_it()
    {
        var action = Pending();
        action.AddSignature(Signature(action, "a.nair", ActionSignatureMeaning.Performed));
        action.AddSignature(Signature(action, "r.khan", ActionSignatureMeaning.Verified));

        action.Complete();

        Assert.Equal(PendingActionStatus.Completed, action.Status);
        Assert.NotNull(action.ResolvedAt);
    }

    [Fact]
    public void A_rejection_requires_a_reason()
    {
        var action = Pending();
        action.AddSignature(Signature(action, "a.nair", ActionSignatureMeaning.Performed));

        Assert.Throws<ArgumentException>(() => action.Reject("   "));
    }

    [Fact]
    public void A_verification_after_action_cannot_be_cancelled()
    {
        // It already happened. Cancelling would claim to undo something that didn't get undone
        // — rejecting the verification records the discrepancy instead, which is honest.
        var action = Pending(SecondSignatureTiming.VerificationAfter);

        var ex = Assert.Throws<InvalidOperationException>(action.Cancel);
        Assert.Contains("already taken effect", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_authorisation_before_action_can_be_cancelled()
    {
        var action = Pending(SecondSignatureTiming.AuthorisationBefore);

        action.Cancel();

        Assert.Equal(PendingActionStatus.Cancelled, action.Status);
    }

    [Fact]
    public void A_resolved_action_takes_no_further_signatures()
    {
        var action = Pending();
        action.AddSignature(Signature(action, "a.nair", ActionSignatureMeaning.Performed));
        action.Reject("Copy count doesn't reconcile with the register.");

        Assert.Throws<InvalidOperationException>(() =>
            action.AddSignature(Signature(action, "r.khan", ActionSignatureMeaning.Verified)));
    }

    [Fact]
    public void A_refusal_signature_requires_a_reason()
    {
        var action = Pending();

        Assert.Throws<ArgumentException>(() =>
            Signature(action, "r.khan", ActionSignatureMeaning.Refused));
    }
}

public class SignaturePolicyTests
{
    [Fact]
    public void Destructive_actions_always_require_a_signature()
    {
        var policy = SignaturePolicy.CreateDefault("admin");

        foreach (var action in SignaturePolicy.AlwaysRequireSignature)
        {
            Assert.True(policy.For(action).RequiresSignature, $"{action} must require a signature.");
        }
    }

    [Fact]
    public void The_signature_on_a_destructive_action_cannot_be_removed()
    {
        var policy = SignaturePolicy.CreateDefault("admin");
        var weakened = policy.Points
            .Select(p => p.Action == ControlledAction.RecordDisposition
                ? p with { RequiresSignature = false }
                : p)
            .ToList();

        Assert.Throws<ArgumentException>(() => policy.Update(weakened, "admin"));
    }

    [Fact]
    public void Disposition_requires_authorisation_before_it_happens()
    {
        // A record destroyed before approval cannot be restored when approval is refused.
        var point = SignaturePolicy.CreateDefault("admin").For(ControlledAction.RecordDisposition);

        Assert.True(point.RequiresSecondSignature);
        Assert.Equal(SecondSignatureTiming.AuthorisationBefore, point.Timing);
    }

    [Fact]
    public void High_frequency_actions_are_unsigned_by_default()
    {
        // A signature demanded dozens of times a day stops being a considered act.
        var policy = SignaturePolicy.CreateDefault("admin");

        Assert.False(policy.For(ControlledAction.PrintCopy).RequiresSignature);
        Assert.False(policy.For(ControlledAction.RetrieveCopy).RequiresSignature);
    }

    [Fact]
    public void Every_action_has_a_point()
    {
        var policy = SignaturePolicy.CreateDefault("admin");

        foreach (var action in Enum.GetValues<ControlledAction>())
        {
            Assert.NotNull(policy.For(action));
        }
    }
}
