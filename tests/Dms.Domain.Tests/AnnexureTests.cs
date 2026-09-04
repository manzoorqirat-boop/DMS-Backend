using Dms.Domain.Common;
using Dms.Domain.Entities;
using Dms.Domain.Enums;
using Xunit;

namespace Dms.Domain.Tests;

public class AnnexureTests
{
    private static ControlledDocument Sop() => new(
        "ND-QIC-SOP-0042", "Cleaning of Vessel V-101",
        Uuid7.NewGuid(), Uuid7.NewGuid(), Uuid7.NewGuid(), Uuid7.NewGuid(),
        "working/sop.docx", "a.nair");

    private static ControlledDocument AnnexureOf(ControlledDocument parent, int number = 1) =>
        ControlledDocument.CreateAnnexure(
            parent, number, "Cleaning Record Form", Uuid7.NewGuid(),
            "working/annex.docx", "a.nair");

    [Fact]
    public void The_number_is_derived_from_the_parent()
    {
        // Legible on a printed page with no system access: someone holding a loose form can
        // tell which procedure it belongs to.
        Assert.Equal("ND-QIC-SOP-0042-A1", AnnexureOf(Sop()).DocumentNumber);
    }

    [Fact]
    public void An_annexure_starts_in_its_parents_status()
    {
        var sop = Sop();
        sop.SubmitForReview();

        // Not Draft: an annexure added to a document already in review must not look editable.
        Assert.Equal(DocumentStatus.InReview, AnnexureOf(sop).Status);
    }

    [Fact]
    public void Annexures_cannot_be_nested()
    {
        var annexure = AnnexureOf(Sop());

        Assert.Throws<InvalidOperationException>(() => AnnexureOf(annexure, 2));
    }

    [Theory]
    [InlineData("SubmitForReview")]
    [InlineData("Withdraw")]
    [InlineData("MakeEffective")]
    [InlineData("MakeObsolete")]
    [InlineData("BeginRevision")]
    public void Lifecycle_operations_are_refused_on_an_annexure(string operation)
    {
        // The core invariant: an annexure moves only with its parent. A direct transition
        // could put a form in circulation for a procedure that isn't in force.
        var annexure = AnnexureOf(Sop());
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var ex = Assert.Throws<InvalidOperationException>(() => _ = operation switch
        {
            "SubmitForReview" => Act(annexure.SubmitForReview),
            "Withdraw" => Act(annexure.Withdraw),
            "MakeEffective" => Act(() => annexure.MakeEffective(today, today)),
            "MakeObsolete" => Act(() => annexure.MakeObsolete("no longer needed")),
            "BeginRevision" => Act(() => annexure.BeginRevision("working/next.docx", "a.nair")),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        });

        Assert.Contains("annexure", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("parent", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Act(Action action)
    {
        action();
        return true;
    }

    [Fact]
    public void FollowParent_carries_status_and_dates_across()
    {
        var sop = Sop();
        var annexure = AnnexureOf(sop);

        sop.SubmitForReview();
        sop.MarkApproved("approved/sop.docx", new string('a', 64));

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        sop.MakeEffective(today, today, today.AddMonths(24));

        annexure.FollowParent(sop);

        Assert.Equal(DocumentStatus.Effective, annexure.Status);
        Assert.Equal(sop.EffectiveDate, annexure.EffectiveDate);
        Assert.Equal(sop.NextReviewDate, annexure.NextReviewDate);
    }

    [Fact]
    public void FollowParent_refuses_an_unrelated_document()
    {
        // Guards against cascading from the wrong parent, which would quietly put an annexure
        // into a status its own parent never reached.
        var annexure = AnnexureOf(Sop());

        Assert.Throws<InvalidOperationException>(() => annexure.FollowParent(Sop()));
    }

    [Fact]
    public void FollowParent_refuses_on_a_non_annexure()
    {
        var sop = Sop();

        Assert.Throws<InvalidOperationException>(() => sop.FollowParent(Sop()));
    }
}
