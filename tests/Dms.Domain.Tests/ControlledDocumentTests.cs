using Dms.Domain.Entities;
using Dms.Domain.Enums;
using Xunit;
using Dms.Domain.Common;

namespace Dms.Domain.Tests;

/// <summary>
/// The lifecycle state machine. These transitions decide whether a procedure people are
/// following can be edited, replaced or withdrawn, so the guards matter more than the
/// happy path.
/// </summary>
public class ControlledDocumentTests
{
    private static readonly DateOnly Today = new(2026, 8, 25);

    private static ControlledDocument NewDraft() => new(
        "MNK-QA-SOP-0001",
        "Cleaning of Vessel V-101",
        Uuid7.NewGuid(),
        Uuid7.NewGuid(),
        Uuid7.NewGuid(),
        Uuid7.NewGuid(),
        "documents/abc.docx",
        "a.nair");

    private static ControlledDocument Effective()
    {
        var document = NewDraft();
        document.SubmitForReview("hash-of-content");
        document.MarkApproved();
        document.MakeEffective(Today, Today);
        return document;
    }

    [Fact]
    public void New_document_founds_its_own_lineage_and_is_current()
    {
        var document = NewDraft();

        Assert.Equal(document.Id, document.FamilyId);
        Assert.True(document.IsCurrentRevision);
        Assert.Equal(0, document.Revision);
        Assert.True(document.IsEditable);
    }

    [Fact]
    public void Only_a_draft_is_editable()
    {
        var document = NewDraft();
        document.SubmitForReview("hash");

        Assert.False(document.IsEditable);
    }

    [Fact]
    public void Submitting_freezes_the_content_hash_signatures_apply_to()
    {
        var document = NewDraft();
        document.SubmitForReview("sha256-abc");

        Assert.Equal("sha256-abc", document.ContentHashAtSubmission);
        Assert.Equal(DocumentStatus.InReview, document.Status);
    }

    [Fact]
    public void Returning_for_rework_clears_the_hash_so_the_next_round_rebinds()
    {
        var document = NewDraft();
        document.SubmitForReview("sha256-abc");
        document.ReturnForRework();

        Assert.Equal(DocumentStatus.Draft, document.Status);
        Assert.Null(document.ContentHashAtSubmission);
    }

    [Fact]
    public void A_document_cannot_be_approved_without_going_through_review()
    {
        var document = NewDraft();

        Assert.Throws<InvalidOperationException>(() => document.MarkApproved());
    }

    [Fact]
    public void Effective_date_may_be_in_the_future()
    {
        // The normal case: training and distribution happen before an SOP takes effect.
        var document = NewDraft();
        document.SubmitForReview("hash");
        document.MarkApproved();

        document.MakeEffective(Today.AddDays(30), Today);

        Assert.Equal(DocumentStatus.Effective, document.Status);
    }

    [Fact]
    public void Effective_date_may_not_be_backdated()
    {
        var document = NewDraft();
        document.SubmitForReview("hash");
        document.MarkApproved();

        Assert.Throws<InvalidOperationException>(() => document.MakeEffective(Today.AddDays(-1), Today));
    }

    [Fact]
    public void Revision_keeps_the_number_and_lineage_but_is_not_yet_current()
    {
        var original = Effective();

        var revision = original.BeginRevision("documents/def.docx", "b.singh");

        Assert.Equal(original.DocumentNumber, revision.DocumentNumber);
        Assert.Equal(original.FamilyId, revision.FamilyId);
        Assert.Equal(1, revision.Revision);
        Assert.Equal(DocumentStatus.Draft, revision.Status);

        // The predecessor stays in force until the successor is actually issued.
        Assert.False(revision.IsCurrentRevision);
        Assert.True(original.IsCurrentRevision);
    }

    [Fact]
    public void Only_the_version_in_force_can_be_revised()
    {
        var draft = NewDraft();

        Assert.Throws<InvalidOperationException>(() => draft.BeginRevision("documents/def.docx", "b.singh"));
    }

    [Fact]
    public void Withdrawing_a_draft_burns_the_number_rather_than_deleting_it()
    {
        var document = NewDraft();
        document.Withdraw();

        Assert.Equal(DocumentStatus.Withdrawn, document.Status);
        Assert.Equal("MNK-QA-SOP-0001", document.DocumentNumber);
    }

    [Fact]
    public void Obsoleting_requires_a_reason_and_stops_the_review_clock()
    {
        var document = Effective();
        document.RecordPeriodicReview(Today.AddYears(2), "qa.head");

        document.MakeObsolete("Process discontinued.");

        Assert.Equal(DocumentStatus.Obsolete, document.Status);
        Assert.Equal("Process discontinued.", document.ObsoleteReason);

        // Otherwise a withdrawn document would surface in the overdue report forever, which is
        // how real overdue items get lost.
        Assert.Null(document.NextReviewDate);
    }

    [Fact]
    public void Obsoleting_without_a_reason_is_rejected()
    {
        var document = Effective();

        Assert.Throws<ArgumentException>(() => document.MakeObsolete("   "));
    }

    [Fact]
    public void Periodic_review_only_applies_to_a_document_in_force()
    {
        var document = NewDraft();

        Assert.Throws<InvalidOperationException>(() =>
            document.RecordPeriodicReview(Today.AddYears(2), "qa.head"));
    }

    [Fact]
    public void Retention_clock_starts_once_and_is_not_reset_by_a_later_event()
    {
        // A record whose clock already started must not have it pushed back every time
        // something touches it, or retention would extend indefinitely.
        var document = Effective();
        document.MakeObsolete("Superseded by a new process.");

        document.StartRetention(Today.AddYears(5));
        document.StartRetention(Today.AddYears(10));

        Assert.Equal(Today.AddYears(5), document.RetainUntil);
    }

    [Fact]
    public void Disposition_cannot_be_recorded_twice()
    {
        var document = Effective();
        document.MakeObsolete("Withdrawn.");
        document.StartRetention(Today.AddYears(5));

        document.RecordDisposition(DispositionAction.DestroyContent, "Retention expired.", "qa.head");

        Assert.NotNull(document.ContentDestroyedAt);
        Assert.Throws<InvalidOperationException>(() =>
            document.RecordDisposition(DispositionAction.RetainPermanently, "Changed mind.", "qa.head"));
    }

    [Fact]
    public void Disposition_is_rejected_while_the_document_is_still_in_use()
    {
        var document = Effective();

        Assert.Throws<InvalidOperationException>(() =>
            document.RecordDisposition(DispositionAction.DestroyContent, "Too early.", "qa.head"));
    }
}
