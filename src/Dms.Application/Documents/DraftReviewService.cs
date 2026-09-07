using Dms.Application.Abstractions;
using Dms.Application.Common;
using Dms.Domain.Entities;
using Dms.Domain.Enums;

namespace Dms.Application.Documents;

/// <summary>
/// The collaborative draft review: circulating a document to reviewers who may edit it directly
/// and leave comments, before it goes near the signature route.
/// <para>
/// Distinct from <c>ReviewWorkflowService</c>, which runs the formal signed approval. The
/// difference that matters is the content freeze — here the text is still being written, so
/// reviewers fix what they find rather than writing a note asking someone else to.
/// </para>
/// </summary>
public sealed class DraftReviewService(
    IControlledDocumentRepository documents,
    IReviewCommentRepository comments,
    IAccessControl access,
    IAuditTrail audit,
    ICurrentUser currentUser)
{
    private const string EntityType = "ControlledDocument";

    public Task<Result<DocumentSummary>> StartAsync(
        Guid documentId,
        CancellationToken cancellationToken) =>
        TransitionAsync(documentId, start: true, cancellationToken);

    public Task<Result<DocumentSummary>> CloseAsync(
        Guid documentId,
        CancellationToken cancellationToken) =>
        TransitionAsync(documentId, start: false, cancellationToken);

    private async Task<Result<DocumentSummary>> TransitionAsync(
        Guid documentId,
        bool start,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserName is not { } actor || string.IsNullOrWhiteSpace(actor))
        {
            return Error.Validation("actor_unknown", "The acting user could not be determined.");
        }

        var document = await documents.GetAsync(documentId, cancellationToken);
        if (document is null)
        {
            return Error.NotFound("document_not_found", $"No document with id {documentId}.");
        }

        var permitted = await access.HasPermissionAsync(
            Permission.DocumentEdit, document.SiteId, document.DepartmentId, cancellationToken);

        if (!permitted)
        {
            return Error.Validation(
                "permission_denied",
                $"{Permission.DocumentEdit} is required for this document's site and department.");
        }

        try
        {
            if (start)
            {
                document.StartDraftReview();
            }
            else
            {
                document.CloseDraftReview();
            }
        }
        catch (InvalidOperationException ex)
        {
            return Error.Conflict("draft_review_transition_refused", ex.Message);
        }

        var openBlocking = await comments.CountOpenBlockingAsync(document.Id, cancellationToken);

        audit.Record(
            start ? AuditAction.DocumentSubmittedForReview : AuditAction.DocumentReturnedForRework,
            EntityType, document.Id, $"{document.DocumentNumber} Rev {document.Revision:00}",
            start
                ? "Circulated for collaborative draft review. Reviewers may edit and comment."
                : $"Draft review round closed with {openBlocking} blocking comment(s) still open.");

        var outcome = await documents.SaveChangesAsync(cancellationToken);

        return outcome.Saved
            ? DocumentSummary.From(document)
            : Error.Conflict("document_save_conflict", "The document could not be updated.");
    }

    /// <summary>
    /// Raises a comment against part of the document.
    /// <para>
    /// Permitted in the signature route as well as in draft review: a reviewer rejecting a
    /// document needs to say what is wrong with it, and that is exactly when an anchored comment
    /// is most useful. What differs is what happens next — in draft review the reviewer may fix
    /// it themselves; in the signature route they cannot.
    /// </para>
    /// </summary>
    public async Task<Result<ReviewCommentView>> AddCommentAsync(
        Guid documentId,
        AddReviewCommentRequest request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserName is not { } actor || string.IsNullOrWhiteSpace(actor))
        {
            return Error.Validation("actor_unknown", "The acting user could not be determined.");
        }

        var document = await documents.GetAsync(documentId, cancellationToken);
        if (document is null)
        {
            return Error.NotFound("document_not_found", $"No document with id {documentId}.");
        }

        var permitted = await access.HasPermissionAsync(
            Permission.DocumentView, document.SiteId, document.DepartmentId, cancellationToken);

        if (!permitted)
        {
            return Error.Validation(
                "permission_denied", $"{Permission.DocumentView} is required for this document.");
        }

        // Commenting on an approved or effective document would be raising an issue against
        // something already in force — that is a change request, not a review comment. Keeping
        // them apart stops a comment thread becoming a shadow change-control process.
        if (document.Status is not (DocumentStatus.Draft
            or DocumentStatus.InDraftReview or DocumentStatus.InReview))
        {
            return Error.Conflict(
                "not_under_review",
                $"{document.DocumentNumber} is {document.Status}. Comments can only be raised while "
                + "a document is being drafted or reviewed — revise it to change something already "
                + "approved.");
        }

        ReviewComment comment;
        try
        {
            comment = new ReviewComment(
                document.Id, request.SectionReference, request.QuotedText,
                request.Body, request.Severity, actor);
        }
        catch (ArgumentException ex)
        {
            return Error.Validation("comment_invalid", ex.Message);
        }

        comments.Add(comment);

        audit.Record(
            AuditAction.DocumentReturnedForRework, EntityType, document.Id,
            $"{document.DocumentNumber} Rev {document.Revision:00}",
            $"{request.Severity} comment raised on {request.SectionReference} by {actor}.");

        var outcome = await comments.SaveChangesAsync(cancellationToken);

        return outcome.Saved
            ? ReviewCommentView.From(comment)
            : Error.Conflict("comment_save_conflict", "The comment could not be saved.");
    }

    /// <summary>
    /// Answers a comment — either acting on it or declining it with a reason.
    /// <para>
    /// Both count as answered and both clear the block. Declining is legitimate: a reviewer can
    /// be wrong, and recording the disagreement is more honest than complying under protest or
    /// quietly ignoring it.
    /// </para>
    /// </summary>
    public async Task<Result<ReviewCommentView>> RespondAsync(
        Guid commentId,
        RespondToCommentRequest request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserName is not { } actor || string.IsNullOrWhiteSpace(actor))
        {
            return Error.Validation("actor_unknown", "The acting user could not be determined.");
        }

        var comment = await comments.GetAsync(commentId, cancellationToken);
        if (comment is null)
        {
            return Error.NotFound("comment_not_found", "That comment no longer exists.");
        }

        var document = await documents.GetAsync(comment.DocumentId, cancellationToken);
        if (document is null)
        {
            return Error.NotFound("document_not_found", "The document no longer exists.");
        }

        var permitted = await access.HasPermissionAsync(
            Permission.DocumentEdit, document.SiteId, document.DepartmentId, cancellationToken);

        if (!permitted)
        {
            return Error.Validation(
                "permission_denied",
                $"{Permission.DocumentEdit} is required to answer comments on this document.");
        }

        try
        {
            if (request.Accept)
            {
                comment.Resolve(request.Response, actor);
            }
            else
            {
                comment.Decline(request.Response, actor);
            }
        }
        catch (InvalidOperationException ex)
        {
            return Error.Conflict("comment_already_answered", ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Error.Validation("response_required", ex.Message);
        }

        audit.Record(
            AuditAction.DocumentReturnedForRework, EntityType, document.Id,
            $"{document.DocumentNumber} Rev {document.Revision:00}",
            $"Comment on {comment.SectionReference} {(request.Accept ? "resolved" : "declined")} "
            + $"by {actor}: {request.Response}");

        var outcome = await comments.SaveChangesAsync(cancellationToken);

        return outcome.Saved
            ? ReviewCommentView.From(comment)
            : Error.Conflict("comment_save_conflict", "The response could not be saved.");
    }

    /// <summary>
    /// The reviewer withdraws their comment, or reopens one whose answer does not satisfy them.
    /// <para>
    /// The entity restricts both to whoever raised it, and that is the point: an author who
    /// could withdraw comments about their own document could clear every blocker without
    /// addressing anything.
    /// </para>
    /// </summary>
    public async Task<Result<ReviewCommentView>> ReviewerActionAsync(
        Guid commentId,
        bool reopen,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserName is not { } actor || string.IsNullOrWhiteSpace(actor))
        {
            return Error.Validation("actor_unknown", "The acting user could not be determined.");
        }

        var comment = await comments.GetAsync(commentId, cancellationToken);
        if (comment is null)
        {
            return Error.NotFound("comment_not_found", "That comment no longer exists.");
        }

        try
        {
            if (reopen)
            {
                comment.Reopen(actor);
            }
            else
            {
                comment.Withdraw(actor);
            }
        }
        catch (InvalidOperationException ex)
        {
            return Error.Conflict("comment_action_refused", ex.Message);
        }

        var document = await documents.GetAsync(comment.DocumentId, cancellationToken);

        audit.Record(
            AuditAction.DocumentReturnedForRework, EntityType, comment.DocumentId,
            document?.DocumentNumber ?? comment.DocumentId.ToString(),
            $"Comment on {comment.SectionReference} {(reopen ? "reopened" : "withdrawn")} by {actor}.");

        var outcome = await comments.SaveChangesAsync(cancellationToken);

        return outcome.Saved
            ? ReviewCommentView.From(comment)
            : Error.Conflict("comment_save_conflict", "The comment could not be updated.");
    }

    public async Task<Result<IReadOnlyList<ReviewCommentView>>> ListCommentsAsync(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var document = await documents.GetAsync(documentId, cancellationToken);
        if (document is null)
        {
            return Error.NotFound("document_not_found", $"No document with id {documentId}.");
        }

        var permitted = await access.HasPermissionAsync(
            Permission.DocumentView, document.SiteId, document.DepartmentId, cancellationToken);

        if (!permitted)
        {
            return Error.Validation(
                "permission_denied", $"{Permission.DocumentView} is required for this document.");
        }

        var found = await comments.ListForDocumentAsync(documentId, cancellationToken);

        return Result<IReadOnlyList<ReviewCommentView>>.Success(
            found.Select(ReviewCommentView.From).ToList());
    }
}
