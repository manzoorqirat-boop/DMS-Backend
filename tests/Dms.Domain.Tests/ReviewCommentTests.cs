using Dms.Domain.Common;
using Dms.Domain.Entities;
using Dms.Domain.Enums;
using Xunit;

namespace Dms.Domain.Tests;

public class ReviewCommentTests
{
    private static ReviewComment Comment(CommentSeverity severity = CommentSeverity.Blocking) =>
        new(Uuid7.NewGuid(), "6.3", "Rinse the vessel twice.",
            "Twice is not enough for product changeover.", severity, "r.khan");

    [Fact]
    public void A_blocking_comment_blocks_resubmission()
    {
        Assert.True(Comment(CommentSeverity.Blocking).BlocksResubmission);
    }

    [Fact]
    public void An_advisory_comment_does_not()
    {
        // Leaving one open is a considered decision. Forcing a response to every stylistic note
        // turns the mechanism into something people click through.
        Assert.False(Comment(CommentSeverity.Advisory).BlocksResubmission);
    }

    [Fact]
    public void Resolving_requires_saying_what_was_done()
    {
        var comment = Comment();

        Assert.Throws<ArgumentException>(() => comment.Resolve("   ", "a.nair"));
    }

    [Fact]
    public void A_resolved_comment_stops_blocking()
    {
        var comment = Comment();

        comment.Resolve("Changed to three rinses in 6.3.", "a.nair");

        Assert.Equal(CommentStatus.Resolved, comment.Status);
        Assert.False(comment.BlocksResubmission);
    }

    [Fact]
    public void Declining_also_answers_it()
    {
        // A reviewer can be wrong. Recording the disagreement is more honest than either
        // ignoring the comment or complying under protest.
        var comment = Comment();

        comment.Decline("Two rinses is validated for this product family.", "a.nair");

        Assert.Equal(CommentStatus.Declined, comment.Status);
        Assert.False(comment.BlocksResubmission);
    }

    [Fact]
    public void Only_the_reviewer_can_withdraw_their_comment()
    {
        var comment = Comment();

        var ex = Assert.Throws<InvalidOperationException>(() => comment.Withdraw("a.nair"));
        Assert.Contains("r.khan", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unsatisfied_reviewer_can_reopen_an_answered_comment()
    {
        // The loop's other direction: reopening keeps the history on one comment rather than
        // starting a fresh one that loses it.
        var comment = Comment();
        comment.Resolve("Reworded.", "a.nair");

        comment.Reopen("r.khan");

        Assert.Equal(CommentStatus.Open, comment.Status);
        Assert.True(comment.BlocksResubmission);
    }

    [Fact]
    public void Only_the_reviewer_can_reopen()
    {
        var comment = Comment();
        comment.Resolve("Reworded.", "a.nair");

        Assert.Throws<InvalidOperationException>(() => comment.Reopen("a.nair"));
    }

    [Fact]
    public void An_open_comment_cannot_be_reopened()
    {
        Assert.Throws<InvalidOperationException>(() => Comment().Reopen("r.khan"));
    }
}

public class DraftReviewTests
{
    private static ControlledDocument Draft() => new(
        "ND-QA-SOP-0001", "Cleaning", Uuid7.NewGuid(), Uuid7.NewGuid(),
        Uuid7.NewGuid(), Uuid7.NewGuid(), "working/sop.docx", "a.nair");

    [Fact]
    public void A_document_in_draft_review_is_still_editable()
    {
        // The whole point of the two workflows: reviewers in the draft phase fix what they
        // find rather than writing a note asking someone else to.
        var doc = Draft();
        doc.StartDraftReview();

        Assert.Equal(DocumentStatus.InDraftReview, doc.Status);
        Assert.True(doc.IsEditable);
    }

    [Fact]
    public void A_document_in_the_signature_route_is_not()
    {
        var doc = Draft();
        doc.SubmitForReview();

        Assert.Equal(DocumentStatus.InReview, doc.Status);
        Assert.False(doc.IsEditable);
    }

    [Fact]
    public void Draft_review_must_be_closed_before_submitting()
    {
        // Ending collaborative review is a decision the author makes, not a side effect.
        var doc = Draft();
        doc.StartDraftReview();

        Assert.Throws<InvalidOperationException>(() => doc.SubmitForReview());

        doc.CloseDraftReview();
        doc.SubmitForReview();

        Assert.Equal(DocumentStatus.InReview, doc.Status);
    }

    [Fact]
    public void Open_blocking_comments_prevent_submission()
    {
        var doc = Draft();

        var ex = Assert.Throws<InvalidOperationException>(() => doc.SubmitForReview(2));
        Assert.Contains("2 blocking", ex.Message, StringComparison.Ordinal);
        Assert.Equal(DocumentStatus.Draft, doc.Status);
    }

    [Fact]
    public void Advisory_comments_do_not_prevent_submission()
    {
        var doc = Draft();

        doc.SubmitForReview(openBlockingComments: 0);

        Assert.Equal(DocumentStatus.InReview, doc.Status);
    }
}
