using Dms.Domain.Entities;
using Dms.Domain.Enums;

namespace Dms.Application.Documents;

public sealed record CreateRetentionPolicyRequest(
    Guid DocumentTypeId,
    Guid? SiteId,
    int RetentionYears,
    RetentionTrigger Trigger);

public sealed record UpdateRetentionPolicyRequest(int RetentionYears, RetentionTrigger Trigger);

public sealed record RetentionPolicyView(
    Guid Id,
    Guid DocumentTypeId,
    string DocumentTypeCode,
    Guid? SiteId,
    int RetentionYears,
    RetentionTrigger Trigger,
    string Scope,
    string CreatedBy,
    DateTimeOffset CreatedAt)
{
    public static RetentionPolicyView From(RetentionPolicy policy, string documentTypeCode) => new(
        policy.Id,
        policy.DocumentTypeId,
        documentTypeCode,
        policy.SiteId,
        policy.RetentionYears,
        policy.Trigger,
        policy.SiteId is null ? "All sites" : "Site override",
        policy.CreatedBy,
        policy.CreatedAt);
}

/// <summary>
/// A record eligible for disposition. <paramref name="DaysOverdue"/> counts how long it has
/// been sitting past its retention expiry without a decision — a growing number here is a
/// records-management backlog, not an emergency.
/// </summary>
public sealed record DispositionDueView(
    Guid DocumentId,
    string DocumentNumber,
    string Title,
    int Revision,
    DocumentStatus Status,
    DateOnly RetainUntil,
    int DaysOverdue,
    string? ObsoleteReason);

/// <param name="Password">
/// Required: RecordDisposition cannot have its signature configured away, and it defaults to
/// needing authorisation before it takes effect. A record destroyed before approval cannot be
/// restored when approval is refused.
/// </param>
public sealed record RecordDispositionRequest(
    DispositionAction Action, string Note, string? Password = null);
