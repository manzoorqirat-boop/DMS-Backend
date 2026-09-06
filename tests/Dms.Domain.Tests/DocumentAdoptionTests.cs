using Dms.Domain.Common;
using Dms.Domain.Entities;
using Dms.Domain.Enums;
using Xunit;

namespace Dms.Domain.Tests;

public class DocumentAdoptionTests
{
    private static readonly DateOnly Today = new(2026, 9, 6);

    private static ControlledDocument GlobalSop(out Guid owningSite)
    {
        owningSite = Uuid7.NewGuid();
        var doc = new ControlledDocument(
            "CORP-QA-SOP-0001", "Deviation Handling",
            owningSite, Uuid7.NewGuid(), Uuid7.NewGuid(), Uuid7.NewGuid(),
            "working/sop.docx", "corporate.qa");

        doc.SetScope(DocumentScope.Global);
        doc.SubmitForReview();
        doc.MarkApproved("approved/sop.docx", new string('a', 64));
        doc.MakeEffective(Today, Today);
        doc.PromoteToCurrent();

        return doc;
    }

    [Fact]
    public void Scope_can_only_be_set_on_a_draft()
    {
        // Making a document global after review would extend its reach to sites whose
        // reviewers never saw it.
        var doc = GlobalSop(out _);

        Assert.Throws<InvalidOperationException>(() => doc.SetScope(DocumentScope.Local));
    }

    [Fact]
    public void A_local_document_cannot_be_adopted()
    {
        var doc = new ControlledDocument(
            "ND-QA-SOP-0001", "Local Procedure",
            Uuid7.NewGuid(), Uuid7.NewGuid(), Uuid7.NewGuid(), Uuid7.NewGuid(),
            "working/sop.docx", "a.nair");

        Assert.False(doc.CanBeAdoptedBy(Uuid7.NewGuid()));
    }

    [Fact]
    public void The_owning_site_cannot_adopt_its_own_document()
    {
        // It is already in force there through its own lifecycle. An adoption record would
        // imply the site had to accept its own document.
        var doc = GlobalSop(out var owningSite);

        Assert.False(doc.CanBeAdoptedBy(owningSite));
        Assert.True(doc.CanBeAdoptedBy(Uuid7.NewGuid()));
    }

    [Fact]
    public void A_draft_global_document_cannot_be_adopted()
    {
        // Adopting a draft would let a site commit to text approved nowhere.
        var doc = new ControlledDocument(
            "CORP-QA-SOP-0002", "Not Yet Approved",
            Uuid7.NewGuid(), Uuid7.NewGuid(), Uuid7.NewGuid(), Uuid7.NewGuid(),
            "working/sop.docx", "corporate.qa");
        doc.SetScope(DocumentScope.Global);

        Assert.False(doc.CanBeAdoptedBy(Uuid7.NewGuid()));
    }

    [Fact]
    public void A_revision_stays_global()
    {
        // Losing scope on revision would quietly turn a corporate document local, and every
        // adopting site would find it gone from their register with nothing to explain why.
        var doc = GlobalSop(out _);
        var next = doc.BeginRevision("working/rev2.docx", "corporate.qa");

        Assert.Equal(DocumentScope.Global, next.Scope);
    }

    [Fact]
    public void An_annexure_inherits_its_parents_scope()
    {
        var doc = new ControlledDocument(
            "CORP-QA-SOP-0003", "With Forms",
            Uuid7.NewGuid(), Uuid7.NewGuid(), Uuid7.NewGuid(), Uuid7.NewGuid(),
            "working/sop.docx", "corporate.qa");
        doc.SetScope(DocumentScope.Global);

        var annexure = ControlledDocument.CreateAnnexure(
            doc, 1, "Deviation Form", Uuid7.NewGuid(), "working/annex.docx", "corporate.qa");

        Assert.Equal(DocumentScope.Global, annexure.Scope);
    }

    [Fact]
    public void An_adoption_cannot_be_backdated()
    {
        // Backdating would let a site claim staff worked to a procedure before it said so.
        Assert.Throws<ArgumentException>(() => new DocumentAdoption(
            Uuid7.NewGuid(), Uuid7.NewGuid(), Today.AddDays(-1), Today, "site.qa", null));
    }

    [Fact]
    public void An_adoption_may_take_effect_later()
    {
        // The gap is the training window, and it is the reason adoption exists at all.
        var adoption = new DocumentAdoption(
            Uuid7.NewGuid(), Uuid7.NewGuid(), Today.AddDays(30), Today, "site.qa", "After training.");

        Assert.False(adoption.IsInForceOn(Today));
        Assert.True(adoption.IsInForceOn(Today.AddDays(30)));
    }

    [Fact]
    public void Withdrawing_requires_a_reason()
    {
        var adoption = new DocumentAdoption(
            Uuid7.NewGuid(), Uuid7.NewGuid(), Today, Today, "site.qa", null);

        Assert.Throws<ArgumentException>(() => adoption.Withdraw("site.qa", "  "));
    }

    [Fact]
    public void A_withdrawn_adoption_is_no_longer_in_force()
    {
        var adoption = new DocumentAdoption(
            Uuid7.NewGuid(), Uuid7.NewGuid(), Today, Today, "site.qa", null);

        adoption.Withdraw("site.qa", "Superseded by Rev 02, which needs adopting afresh.");

        Assert.False(adoption.IsActive);
        Assert.False(adoption.IsInForceOn(Today));
    }
}
