using Dms.Domain.Common;
using Dms.Domain.Entities;
using Dms.Domain.Enums;
using Xunit;

namespace Dms.Domain.Tests;

public class SuspensionTests
{
    private static readonly DateOnly Today = new(2026, 9, 8);

    private static ControlledDocument Effective()
    {
        var doc = new ControlledDocument(
            "ND-QA-SOP-0001", "Cleaning", Uuid7.NewGuid(), Uuid7.NewGuid(),
            Uuid7.NewGuid(), Uuid7.NewGuid(), "working/sop.docx", "a.nair");

        doc.SubmitForReview();
        doc.MarkApproved("approved/sop.docx", new string('a', 64));
        doc.MakeEffective(Today, Today);
        doc.PromoteToCurrent();
        return doc;
    }

    [Fact]
    public void Only_an_effective_document_can_be_suspended()
    {
        var draft = new ControlledDocument(
            "ND-QA-SOP-0002", "Draft", Uuid7.NewGuid(), Uuid7.NewGuid(),
            Uuid7.NewGuid(), Uuid7.NewGuid(), "working/sop.docx", "a.nair");

        Assert.Throws<InvalidOperationException>(() => draft.Suspend("Investigating."));
    }

    [Fact]
    public void Suspending_requires_a_reason()
    {
        Assert.Throws<ArgumentException>(() => Effective().Suspend("   "));
    }

    [Fact]
    public void A_suspended_document_records_why()
    {
        var doc = Effective();

        doc.Suspend("Step 6.3 conflicts with the validated cleaning cycle.");

        Assert.Equal(DocumentStatus.Suspended, doc.Status);
        Assert.Contains("6.3", doc.SuspensionReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Reinstating_returns_it_to_force_and_clears_the_reason()
    {
        var doc = Effective();
        doc.Suspend("Investigating.");

        doc.Reinstate();

        Assert.Equal(DocumentStatus.Effective, doc.Status);
        Assert.Null(doc.SuspensionReason);
    }

    [Fact]
    public void The_effective_date_survives_a_suspension()
    {
        // The document was in force before and after. Rewriting the date would erase the fact
        // that it ever stopped; the audit trail carries the gap.
        var doc = Effective();
        var before = doc.EffectiveDate;

        doc.Suspend("Investigating.");
        doc.Reinstate();

        Assert.Equal(before, doc.EffectiveDate);
    }

    [Fact]
    public void Only_a_suspended_document_can_be_reinstated()
    {
        Assert.Throws<InvalidOperationException>(Effective().Reinstate);
    }

    [Fact]
    public void A_suspended_document_can_be_revised()
    {
        // Revising is the main way a suspension is resolved. Requiring reinstatement first
        // would mean putting a document known to be wrong back into force to correct it.
        var doc = Effective();
        doc.Suspend("Step 6.3 is wrong.");

        var revision = doc.BeginRevision("working/rev2.docx", "a.nair");

        Assert.Equal(2, revision.Revision);
        Assert.Equal(DocumentStatus.Draft, revision.Status);
    }

    [Fact]
    public void A_suspended_document_can_be_withdrawn()
    {
        // The other way a suspension ends.
        var doc = Effective();
        doc.Suspend("Superseded by a change in the validated process.");

        doc.MakeObsolete("Investigation concluded the procedure is no longer appropriate.");

        Assert.Equal(DocumentStatus.Obsolete, doc.Status);
    }

    [Fact]
    public void A_suspended_document_still_comes_up_for_periodic_review()
    {
        // One that nobody revisits sits in limbo with no scheduled moment where someone has to
        // decide its fate.
        var doc = Effective();
        doc.Suspend("Investigating.");

        doc.RecordPeriodicReview(Today.AddMonths(12), "q.sharma");

        Assert.Equal(Today.AddMonths(12), doc.NextReviewDate);
    }

    [Fact]
    public void An_annexure_cannot_be_suspended_directly()
    {
        var parent = Effective();
        var annexure = ControlledDocument.CreateAnnexure(
            parent, 1, "Form", Uuid7.NewGuid(), "working/annex.docx", "a.nair");

        Assert.Throws<InvalidOperationException>(() => annexure.Suspend("Investigating."));
    }

    [Fact]
    public void The_suspended_stamp_warns_against_use_and_cannot_be_disabled()
    {
        var stamps = DocumentStatusStamps.CreateDefault("admin");

        Assert.Contains(DocumentStatus.Suspended, DocumentStatusStamps.RequiresWarning);
        Assert.Contains("DO NOT USE", stamps.For(DocumentStatus.Suspended).Text,
            StringComparison.OrdinalIgnoreCase);
    }
}
