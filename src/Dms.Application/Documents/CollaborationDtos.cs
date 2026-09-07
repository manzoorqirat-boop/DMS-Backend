using Dms.Domain.Entities;
using Dms.Domain.Enums;

namespace Dms.Application.Documents;

/// <param name="EffectiveDate">
/// When the document takes effect at the adopting site — usually later than the owning site's,
/// and the gap is the training window. The domain refuses a date in the past.
/// </param>
public sealed record AdoptDocumentRequest(Guid SiteId, DateOnly EffectiveDate, string? Note);

public sealed record WithdrawAdoptionRequest(string Reason);

public sealed record AdoptionView(
    Guid Id,
    Guid DocumentId,
    string DocumentNumber,
    string Title,
    int Revision,
    Guid SiteId,
    DateOnly EffectiveDate,
    string AdoptedBy,
    DateTimeOffset AdoptedAt,
    string? Note,
    bool IsActive,
    string? WithdrawnBy,
    DateTimeOffset? WithdrawnAt,
    string? WithdrawalReason)
{
    public static AdoptionView From(DocumentAdoption adoption, ControlledDocument? document) => new(
        adoption.Id,
        adoption.DocumentId,
        document?.DocumentNumber ?? "(unknown)",
        document?.Title ?? "(unknown)",
        document?.Revision ?? 0,
        adoption.SiteId,
        adoption.EffectiveDate,
        adoption.AdoptedBy,
        adoption.AdoptedAt,
        adoption.Note,
        adoption.IsActive,
        adoption.WithdrawnBy,
        adoption.WithdrawnAt,
        adoption.WithdrawalReason);
}

/// <param name="SectionReference">Where in the document — "6.3", "Section 4".</param>
/// <param name="QuotedText">
/// The text being commented on, verbatim. Optional: some comments are about something missing,
/// which has no text to quote.
/// </param>
public sealed record AddReviewCommentRequest(
    string SectionReference,
    string? QuotedText,
    string Body,
    CommentSeverity Severity);

/// <param name="Accept">
/// True resolves the comment (acted on it), false declines it (disagreed, with a reason). Both
/// count as answered and both clear the block.
/// </param>
public sealed record RespondToCommentRequest(bool Accept, string Response);

public sealed record ReviewCommentView(
    Guid Id,
    Guid DocumentId,
    string SectionReference,
    string? QuotedText,
    string Body,
    CommentSeverity Severity,
    CommentStatus Status,
    bool BlocksResubmission,
    string RaisedBy,
    DateTimeOffset RaisedAt,
    string? Response,
    string? RespondedBy,
    DateTimeOffset? RespondedAt)
{
    public static ReviewCommentView From(ReviewComment comment) => new(
        comment.Id,
        comment.DocumentId,
        comment.SectionReference,
        comment.QuotedText,
        comment.Body,
        comment.Severity,
        comment.Status,
        comment.BlocksResubmission,
        comment.RaisedBy,
        comment.RaisedAt,
        comment.Response,
        comment.RespondedBy,
        comment.RespondedAt);
}
