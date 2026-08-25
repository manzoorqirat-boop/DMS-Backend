using Dms.Application.Abstractions;
using Dms.Application.Common;
using Dms.Domain.Entities;
using Dms.Domain.Enums;

namespace Dms.Application.Documents;

/// <summary>
/// The end of the lifecycle: periodic review, and withdrawal from use.
/// <para>
/// Also owns review-policy resolution, so issuance can ask one place what a document's next
/// review date should be.
/// </para>
/// </summary>
public sealed class DocumentLifecycleService(
    IControlledDocumentRepository documents,
    IReviewPolicyRepository policies,
    IDocumentTypeRepository documentTypes,
    IAccessControl access,
    RetentionService retention,
    IAuditTrail audit,
    ICurrentUser currentUser)
{
    private const string EntityType = "ControlledDocument";
    private const string PolicyEntityType = "ReviewPolicy";

    /// <summary>
    /// The next review date for a document becoming effective, or null when no policy applies.
    /// <para>
    /// Null rather than a default interval: inventing a review cycle nobody configured would
    /// fill the overdue report with documents whose owners never agreed they expire, and the
    /// report is only useful if everything in it is genuinely actionable.
    /// </para>
    /// </summary>
    public async Task<DateOnly?> ResolveNextReviewDateAsync(
        Guid documentTypeId,
        Guid siteId,
        DateOnly effectiveDate,
        CancellationToken cancellationToken)
    {
        var candidates = await policies.FindCandidatesAsync(documentTypeId, siteId, cancellationToken);

        var policy = candidates.OrderByDescending(p => p.Specificity).FirstOrDefault();
        return policy?.DueDateFrom(effectiveDate);
    }

    /// <summary>
    /// Records a periodic review that concluded the document is still correct as written,
    /// pushing the due date out by another interval.
    /// <para>
    /// <b>Worth deciding before go-live:</b> in most pharma DMS implementations a review
    /// concluding "no change required" is itself a signed QA act, not a recorded note. This
    /// records it with an attributable actor, a reason and an audit entry, which is defensible
    /// — but if your customers expect a signature, this should route through the signature
    /// engine rather than being a direct state change. Deliberately left as the simpler option
    /// rather than guessed at.
    /// </para>
    /// </summary>
    public async Task<Result<DocumentSummary>> RecordPeriodicReviewAsync(
        Guid documentId,
        string outcome,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserName is not { } reviewer || string.IsNullOrWhiteSpace(reviewer))
        {
            return Error.Validation(
                "actor_unknown",
                "The acting user could not be determined. Reviews must be attributable.");
        }

        if (string.IsNullOrWhiteSpace(outcome))
        {
            return Error.Validation(
                "review_outcome_required",
                "State the outcome of the review. A review with no recorded conclusion is not a review.");
        }

        var document = await documents.GetAsync(documentId, cancellationToken);
        if (document is null)
        {
            return Error.NotFound("document_not_found", $"No document with id {documentId}.");
        }

        var permitted = await access.HasPermissionAsync(
            Permission.DocumentIssue, document.SiteId, document.DepartmentId, cancellationToken);

        if (!permitted)
        {
            return Error.Validation(
                "permission_denied",
                $"{Permission.DocumentIssue} is required for this document's site and department.");
        }

        var previousDue = document.NextReviewDate;

        // Measured from today rather than from the old due date, so a review completed six
        // months late doesn't immediately fall due again.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var nextDue = await ResolveNextReviewDateAsync(
            document.DocumentTypeId, document.SiteId, today, cancellationToken);

        if (nextDue is null)
        {
            return Error.Conflict(
                "no_review_policy",
                "No review policy applies to this document type, so there is no interval to extend.");
        }

        try
        {
            document.RecordPeriodicReview(nextDue.Value, reviewer);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Error.Conflict("document_not_reviewable", ex.Message);
        }

        audit.Record(
            AuditAction.DocumentPeriodicReviewRecorded, EntityType, document.Id,
            $"{document.DocumentNumber} Rev {document.Revision:00}",
            $"Reviewed, no revision required. Due {previousDue:yyyy-MM-dd} → {nextDue:yyyy-MM-dd}. "
            + $"Outcome: {outcome.Trim()}");

        var saved = await documents.SaveChangesAsync(cancellationToken);
        return saved.Saved
            ? DocumentSummary.From(document)
            : Error.Conflict("document_save_conflict", "The review could not be recorded.");
    }

    /// <summary>
    /// Withdraws a document from use with no replacement.
    /// <para>
    /// Terminal and deliberately not a delete: the record is retained for its retention period,
    /// and anything that cites it must still resolve. Permitted from Effective (withdrawn
    /// outright) or Superseded (an old revision being retired from the register).
    /// </para>
    /// </summary>
    public async Task<Result<DocumentSummary>> MakeObsoleteAsync(
        Guid documentId,
        string reason,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentUser.UserName))
        {
            return Error.Validation(
                "actor_unknown",
                "The acting user could not be determined. Withdrawal must be attributable.");
        }

        var document = await documents.GetAsync(documentId, cancellationToken);
        if (document is null)
        {
            return Error.NotFound("document_not_found", $"No document with id {documentId}.");
        }

        var permitted = await access.HasPermissionAsync(
            Permission.DocumentObsolete, document.SiteId, document.DepartmentId, cancellationToken);

        if (!permitted)
        {
            return Error.Validation(
                "permission_denied",
                $"{Permission.DocumentObsolete} is required for this document's site and department.");
        }

        var wasCurrent = document.IsCurrentRevision;

        try
        {
            document.MakeObsolete(reason);

            // A withdrawn document is no longer what anyone should be following, so it stops
            // being the current revision. That leaves the family with none — which is correct
            // and honest: there is no procedure in force for this any more.
            if (wasCurrent)
            {
                document.StandDown();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Error.Conflict("document_not_obsoletable", ex.Message);
        }

        // Retention runs from the moment the record leaves active use.
        await retention.StartRetentionAsync(document, RetentionTrigger.Obsolete, cancellationToken);

        audit.Record(
            AuditAction.DocumentObsoleted, EntityType, document.Id,
            $"{document.DocumentNumber} Rev {document.Revision:00}",
            wasCurrent
                ? $"Withdrawn from use with no replacement. Reason: {reason.Trim()}"
                : $"Superseded revision retired. Reason: {reason.Trim()}");

        var saved = await documents.SaveChangesAsync(cancellationToken);
        return saved.Saved
            ? DocumentSummary.From(document)
            : Error.Conflict("document_save_conflict", "The document could not be withdrawn.");
    }

    /// <summary>
    /// Documents due or overdue for review. <paramref name="withinDays"/> looks ahead — the
    /// pre-intimation window — while anything already past its date is included regardless and
    /// sorts to the top.
    /// </summary>
    public async Task<IReadOnlyList<ReviewDueView>> ListDueForReviewAsync(
        int withinDays,
        Guid? siteId,
        Guid? departmentId,
        CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var horizon = today.AddDays(Math.Clamp(withinDays, 0, 3650));

        var due = await documents.ListDueForReviewAsync(horizon, siteId, departmentId, cancellationToken);

        return due
            .Select(d => new ReviewDueView(
                d.Id,
                d.DocumentNumber,
                d.Title,
                d.Revision,
                d.EffectiveDate,
                d.NextReviewDate!.Value,
                d.NextReviewDate!.Value.DayNumber - today.DayNumber,
                d.NextReviewDate!.Value < today,
                d.LastReviewedAt,
                d.LastReviewedBy))
            .ToList();
    }

    public async Task<Result<ReviewPolicyView>> CreatePolicyAsync(
        CreateReviewPolicyRequest request,
        CancellationToken cancellationToken)
    {
        var gate = await RequireConfigureAsync(request.SiteId, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var documentType = await documentTypes.GetAsync(request.DocumentTypeId, cancellationToken);
        if (documentType is null)
        {
            return Error.NotFound("document_type_not_found", $"No document type with id {request.DocumentTypeId}.");
        }

        ReviewPolicy policy;
        try
        {
            policy = new ReviewPolicy(
                request.DocumentTypeId,
                request.SiteId,
                request.ReviewIntervalMonths,
                currentUser.UserName!);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return Error.Validation("review_policy_invalid", ex.Message);
        }

        policies.Add(policy);
        audit.Record(
            AuditAction.ReviewPolicyCreated, PolicyEntityType, policy.Id, documentType.Code,
            $"Every {policy.ReviewIntervalMonths} months. "
            + "Reminder lead time is configured separately, on the review notification rule.");

        var outcome = await policies.SaveChangesAsync(cancellationToken);
        if (!outcome.Saved)
        {
            return outcome.ViolatedIndexContains("scope")
                ? Error.Conflict(
                    "review_policy_exists",
                    "A review policy already exists for that document type and site. Edit it instead.")
                : Error.Conflict("review_policy_save_conflict", "The policy could not be saved.");
        }

        return ReviewPolicyView.From(policy, documentType.Code);
    }

    /// <summary>
    /// Changes an interval. Documents already effective keep the due date computed at issuance
    /// until their next review — recalculating them all would silently make a batch of
    /// documents overdue, or silently clear a backlog, neither of which anyone asked for.
    /// </summary>
    public async Task<Result<ReviewPolicyView>> UpdatePolicyAsync(
        Guid policyId,
        int reviewIntervalMonths,
        CancellationToken cancellationToken)
    {
        var policy = await policies.GetAsync(policyId, cancellationToken);
        if (policy is null)
        {
            return Error.NotFound("review_policy_not_found", $"No review policy with id {policyId}.");
        }

        var gate = await RequireConfigureAsync(policy.SiteId, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var previous = policy.ReviewIntervalMonths;

        try
        {
            policy.Update(reviewIntervalMonths);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Error.Validation("review_policy_invalid", ex.Message);
        }

        var documentType = await documentTypes.GetAsync(policy.DocumentTypeId, cancellationToken);

        audit.Record(
            AuditAction.ReviewPolicyChanged, PolicyEntityType, policy.Id,
            documentType?.Code ?? policy.DocumentTypeId.ToString(),
            $"Interval {previous} → {policy.ReviewIntervalMonths} months. "
            + "Documents already effective keep their existing due dates.");

        var outcome = await policies.SaveChangesAsync(cancellationToken);
        return outcome.Saved
            ? ReviewPolicyView.From(policy, documentType?.Code ?? "")
            : Error.Conflict("review_policy_save_conflict", "The policy could not be updated.");
    }

    public async Task<IReadOnlyList<ReviewPolicyView>> ListPoliciesAsync(
        Guid? documentTypeId,
        CancellationToken cancellationToken)
    {
        var found = await policies.ListAsync(documentTypeId, cancellationToken);
        var types = await documentTypes.ListAsync(includeInactive: true, cancellationToken);
        var codes = types.ToDictionary(t => t.Id, t => t.Code);

        return found
            .Select(p => ReviewPolicyView.From(p, codes.GetValueOrDefault(p.DocumentTypeId, "")))
            .ToList();
    }

    private async Task<Error?> RequireConfigureAsync(Guid? siteId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentUser.UserName))
        {
            return Error.Validation("actor_unknown", "The acting user could not be determined.");
        }

        var allowed = await access.HasPermissionAsync(
            Permission.WorkflowConfigure, siteId, departmentId: null, cancellationToken);

        return allowed
            ? null
            : Error.Validation(
                "permission_denied",
                $"{Permission.WorkflowConfigure} is required at this scope to configure review policies.");
    }
}
