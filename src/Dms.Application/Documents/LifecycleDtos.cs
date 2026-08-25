using Dms.Domain.Entities;

namespace Dms.Application.Documents;

public sealed record CreateReviewPolicyRequest(
    Guid DocumentTypeId,
    Guid? SiteId,
    int ReviewIntervalMonths);

public sealed record ReviewPolicyView(
    Guid Id,
    Guid DocumentTypeId,
    string DocumentTypeCode,
    Guid? SiteId,
    int ReviewIntervalMonths,
    string Scope,
    string CreatedBy,
    DateTimeOffset CreatedAt)
{
    public static ReviewPolicyView From(ReviewPolicy policy, string documentTypeCode) => new(
        policy.Id,
        policy.DocumentTypeId,
        documentTypeCode,
        policy.SiteId,
        policy.ReviewIntervalMonths,
        policy.SiteId is null ? "All sites" : "Site override",
        policy.CreatedBy,
        policy.CreatedAt);
}

/// <summary>
/// A row in the periodic-review report. <paramref name="DaysUntilDue"/> goes negative once
/// overdue, which is what lets a caller sort or colour by urgency without recomputing dates.
/// </summary>
public sealed record ReviewDueView(
    Guid DocumentId,
    string DocumentNumber,
    string Title,
    int Revision,
    DateOnly? EffectiveDate,
    DateOnly NextReviewDate,
    int DaysUntilDue,
    bool IsOverdue,
    DateTimeOffset? LastReviewedAt,
    string? LastReviewedBy);
