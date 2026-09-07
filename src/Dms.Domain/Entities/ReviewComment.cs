using Dms.Domain.Common;
using Dms.Domain.Enums;

namespace Dms.Domain.Entities;

/// <summary>
/// A reviewer's comment on a specific part of a document, and the author's answer to it.
/// <para>
/// This exists because a rejection carrying one free-text reason tells an author that something
/// is wrong but not where, and leaves no record of whether each point was ever addressed. The
/// valuable part is not the comment — it is the loop: raise, revise, respond, resolve. An
/// inspector asking <i>what did the reviewer object to and how was it dealt with</i> gets an
/// answer from these rows.
/// </para>
/// <para>
/// <b>Anchored by reference, not by position.</b> The reviewer records the section and the
/// sentence they are talking about, rather than an offset into the file. Positional anchors
/// break the moment the paragraph is edited — which is precisely what happens next — and would
/// leave a comment pointing at text that no longer exists. A quoted sentence still makes sense
/// after a rewrite, even when it is the thing that was rewritten.
/// </para>
/// <para>
/// Comments belong to a specific revision. Revising produces a new document row, and its
/// comments start empty: a comment on Rev 01 has been dealt with by Rev 02 existing, and
/// carrying it forward would ask the author to resolve something twice.
/// </para>
/// </summary>
public class ReviewComment : Entity, ITimestamped
{
    private ReviewComment() { }

    public ReviewComment(
        Guid documentId,
        string sectionReference,
        string? quotedText,
        string body,
        CommentSeverity severity,
        string raisedBy)
    {
        DocumentId = documentId;
        SectionReference = RequireNonEmpty(sectionReference, nameof(sectionReference));
        QuotedText = string.IsNullOrWhiteSpace(quotedText) ? null : quotedText.Trim();
        Body = RequireNonEmpty(body, nameof(body));
        Severity = severity;
        RaisedBy = RequireNonEmpty(raisedBy, nameof(raisedBy));

        Status = CommentStatus.Open;
        RaisedAt = DateTimeOffset.UtcNow;
        CreatedAt = RaisedAt;
    }

    public Guid DocumentId { get; private set; }

    /// <summary>Where in the document — "6.3", "Section 4", "the header table".</summary>
    public string SectionReference { get; private set; } = "";

    /// <summary>
    /// The text being commented on, captured verbatim. Optional, because not every comment is
    /// about a sentence — some are about something missing, which has no text to quote.
    /// </summary>
    public string? QuotedText { get; private set; }

    public string Body { get; private set; } = "";
    public CommentSeverity Severity { get; private set; }

    public string RaisedBy { get; private set; } = "";
    public DateTimeOffset RaisedAt { get; private set; }

    public CommentStatus Status { get; private set; }

    /// <summary>What the author did about it. Required to resolve a comment.</summary>
    public string? Response { get; private set; }

    public string? RespondedBy { get; private set; }
    public DateTimeOffset? RespondedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    /// <summary>
    /// Whether this comment stands in the way of resubmission.
    /// <para>
    /// Only blocking comments do. An advisory comment left open is a considered decision — the
    /// author read it and chose not to act — and forcing a response to every stylistic
    /// suggestion turns the mechanism into a formality people click through.
    /// </para>
    /// </summary>
    public bool BlocksResubmission =>
        Severity == CommentSeverity.Blocking && Status == CommentStatus.Open;

    /// <summary>
    /// The author addresses the comment. Requires saying what was done, not merely ticking it.
    /// </summary>
    public void Resolve(string response, string respondedBy)
    {
        if (Status != CommentStatus.Open)
        {
            throw new InvalidOperationException($"This comment is already {Status}.");
        }

        Response = RequireNonEmpty(response, nameof(response));
        RespondedBy = RequireNonEmpty(respondedBy, nameof(respondedBy));
        RespondedAt = DateTimeOffset.UtcNow;
        Status = CommentStatus.Resolved;

        UpdatedAt = RespondedAt;
    }

    /// <summary>
    /// The author disagrees and is not acting on it.
    /// <para>
    /// A real outcome, not a loophole — a reviewer can be wrong, and recording the reasoning is
    /// more honest than either silently ignoring the comment or complying with it under
    /// protest. It still counts as answered, so it clears the block: what it does not do is
    /// pretend the change was made.
    /// </para>
    /// </summary>
    public void Decline(string reason, string respondedBy)
    {
        if (Status != CommentStatus.Open)
        {
            throw new InvalidOperationException($"This comment is already {Status}.");
        }

        Response = RequireNonEmpty(reason, nameof(reason));
        RespondedBy = RequireNonEmpty(respondedBy, nameof(respondedBy));
        RespondedAt = DateTimeOffset.UtcNow;
        Status = CommentStatus.Declined;

        UpdatedAt = RespondedAt;
    }

    /// <summary>The reviewer takes it back. Only they should be able to.</summary>
    public void Withdraw(string withdrawnBy)
    {
        if (Status != CommentStatus.Open)
        {
            throw new InvalidOperationException($"This comment is already {Status}.");
        }

        if (!string.Equals(withdrawnBy, RaisedBy, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Only {RaisedBy}, who raised this comment, can withdraw it. Anyone else should "
                + "resolve or decline it, so the reasoning is recorded.");
        }

        Status = CommentStatus.Withdrawn;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Reopens a comment the author answered but the reviewer is not satisfied with.
    /// <para>
    /// The loop's other direction, and the reason a comment is not simply deleted when
    /// answered: a reviewer who reads the response and disagrees needs to say so against the
    /// same comment rather than raising a fresh one that loses the history.
    /// </para>
    /// </summary>
    public void Reopen(string reopenedBy)
    {
        if (Status is not (CommentStatus.Resolved or CommentStatus.Declined))
        {
            throw new InvalidOperationException(
                $"Only an answered comment can be reopened; this one is {Status}.");
        }

        if (!string.Equals(reopenedBy, RaisedBy, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Only {RaisedBy}, who raised this comment, can reopen it.");
        }

        Status = CommentStatus.Open;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static string RequireNonEmpty(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{name} is required.", name)
            : value.Trim();
}
