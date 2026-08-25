using Dms.Application.Abstractions;
using Dms.Application.Common;
using Dms.Domain.Entities;
using Dms.Domain.Enums;

namespace Dms.Application.Documents;

/// <summary>
/// Retention scheduling and disposition.
/// <para>
/// The governing rule here: <b>nothing is ever destroyed on a timer.</b> Expiry of a retention
/// period makes a record <i>eligible</i> for disposition and puts it on a worklist. A person
/// with authority then records a decision, and only that decision deletes anything. A system
/// that quietly destroyed regulated records on a schedule would be indefensible the first time
/// someone asked who authorised it.
/// </para>
/// </summary>
public sealed class RetentionService(
    IControlledDocumentRepository documents,
    IRetentionPolicyRepository policies,
    IDocumentTypeRepository documentTypes,
    IDocumentFileStore documentFiles,
    IAccessControl access,
    IAuditTrail audit,
    ICurrentUser currentUser,
    IClock clock)
{
    private const string EntityType = "ControlledDocument";
    private const string PolicyEntityType = "RetentionPolicy";

    /// <summary>
    /// The retention expiry for a record leaving active use, or null when no policy applies.
    /// <para>
    /// Null rather than an invented default: a record with no configured schedule is kept
    /// indefinitely, which is the safe direction to fail. Guessing a period would put real
    /// records on a destruction worklist nobody agreed to.
    /// </para>
    /// </summary>
    public async Task<DateOnly?> ResolveRetainUntilAsync(
        Guid documentTypeId,
        Guid siteId,
        RetentionTrigger trigger,
        DateOnly triggeredOn,
        CancellationToken cancellationToken)
    {
        var candidates = await policies.FindCandidatesAsync(documentTypeId, siteId, cancellationToken);

        var policy = candidates
            .Where(p => p.Trigger == trigger)
            .OrderByDescending(p => p.Specificity)
            .FirstOrDefault();

        return policy?.RetainUntil(triggeredOn);
    }

    /// <summary>
    /// Starts a document's retention clock, if a policy applies. Called by whichever service
    /// moved it out of active use.
    /// </summary>
    public async Task<bool> StartRetentionAsync(
        ControlledDocument document,
        RetentionTrigger trigger,
        CancellationToken cancellationToken)
    {
        var retainUntil = await ResolveRetainUntilAsync(
            document.DocumentTypeId, document.SiteId, trigger, clock.Today, cancellationToken);

        if (retainUntil is not { } until)
        {
            return false;
        }

        document.StartRetention(until);

        audit.Record(
            AuditAction.RetentionClockStarted, EntityType, document.Id,
            $"{document.DocumentNumber} Rev {document.Revision:00}",
            $"Triggered by {trigger}. Retain until {until:yyyy-MM-dd}.");

        return true;
    }

    /// <summary>
    /// Records what was decided for a record whose retention has expired, and carries it out.
    /// <para>
    /// For <see cref="DispositionAction.DestroyContent"/> the stored file is deleted but the
    /// register row, its signatures and its audit trail are kept. A retention period permits
    /// destroying the document; it does not permit destroying the evidence that the document
    /// existed, said what it said, and was properly approved.
    /// </para>
    /// </summary>
    public async Task<Result<DocumentSummary>> RecordDispositionAsync(
        Guid documentId,
        DispositionAction action,
        string note,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentUser.UserName))
        {
            return Error.Validation(
                "actor_unknown",
                "The acting user could not be determined. Disposition must be attributable.");
        }

        var document = await documents.GetAsync(documentId, cancellationToken);
        if (document is null)
        {
            return Error.NotFound("document_not_found", $"No document with id {documentId}.");
        }

        // Deliberately the obsolete permission rather than a milder one. Authorising the
        // destruction of a controlled record is at least as consequential as withdrawing it
        // from use.
        var permitted = await access.HasPermissionAsync(
            Permission.DocumentObsolete, document.SiteId, document.DepartmentId, cancellationToken);

        if (!permitted)
        {
            return Error.Validation(
                "permission_denied",
                $"{Permission.DocumentObsolete} is required to record a disposition.");
        }

        if (document.RetainUntil is not { } until)
        {
            return Error.Conflict(
                "no_retention_period",
                $"{document.DocumentNumber} has no retention period set, so it is not eligible for disposition.");
        }

        if (until > clock.Today)
        {
            // Checked rather than left to a caller filtering the worklist correctly. An
            // early destruction is not recoverable.
            return Error.Conflict(
                "retention_not_expired",
                $"{document.DocumentNumber} is retained until {until:yyyy-MM-dd}; "
                + $"{until.DayNumber - clock.Today.DayNumber} day(s) remain.");
        }

        try
        {
            document.RecordDisposition(action, note, CurrentActorOrThrow());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Error.Conflict("disposition_not_permitted", ex.Message);
        }

        audit.Record(
            action == DispositionAction.DestroyContent
                ? AuditAction.DocumentContentDestroyed
                : AuditAction.DocumentRetainedPermanently,
            EntityType, document.Id,
            $"{document.DocumentNumber} Rev {document.Revision:00}",
            $"Retention expired {until:yyyy-MM-dd}. Decision: {action}. {note.Trim()}");

        // Audit and register state commit first. If the blob delete then fails, the record
        // correctly says the content was destroyed and a leftover file can be swept later —
        // the reverse order would risk a destroyed file with no record of who authorised it.
        var saved = await documents.SaveChangesAsync(cancellationToken);
        if (!saved.Saved)
        {
            return Error.Conflict("document_save_conflict", "The disposition could not be recorded.");
        }

        if (action == DispositionAction.DestroyContent)
        {
            await documentFiles.DeleteAsync(document.WorkingCopyKey, cancellationToken);
        }

        return DocumentSummary.From(document);
    }

    /// <summary>
    /// Records eligible for disposition: retention expired, no decision recorded yet.
    /// </summary>
    public async Task<IReadOnlyList<DispositionDueView>> ListDueForDispositionAsync(
        Guid? siteId,
        CancellationToken cancellationToken)
    {
        var due = await documents.ListDueForDispositionAsync(clock.Today, siteId, cancellationToken);

        return due
            .Select(d => new DispositionDueView(
                d.Id,
                d.DocumentNumber,
                d.Title,
                d.Revision,
                d.Status,
                d.RetainUntil!.Value,
                clock.Today.DayNumber - d.RetainUntil!.Value.DayNumber,
                d.ObsoleteReason))
            .ToList();
    }

    public async Task<Result<RetentionPolicyView>> CreatePolicyAsync(
        CreateRetentionPolicyRequest request,
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

        RetentionPolicy policy;
        try
        {
            policy = new RetentionPolicy(
                request.DocumentTypeId,
                request.SiteId,
                request.RetentionYears,
                request.Trigger,
                CurrentActorOrThrow());
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return Error.Validation("retention_policy_invalid", ex.Message);
        }

        policies.Add(policy);
        audit.Record(
            AuditAction.RetentionPolicyCreated, PolicyEntityType, policy.Id, documentType.Code,
            $"{policy.RetentionYears} year(s) from {policy.Trigger}.");

        var outcome = await policies.SaveChangesAsync(cancellationToken);
        if (!outcome.Saved)
        {
            return outcome.ViolatedIndexContains("scope")
                ? Error.Conflict(
                    "retention_policy_exists",
                    "A retention policy already exists for that type, site and trigger. Edit it instead.")
                : Error.Conflict("retention_policy_save_conflict", "The policy could not be saved.");
        }

        return RetentionPolicyView.From(policy, documentType.Code);
    }

    /// <summary>
    /// Changes a retention period. Records whose clock already started keep the expiry computed
    /// then — recalculating would silently bring a batch of records forward for destruction,
    /// which is the one direction that must never happen by accident.
    /// </summary>
    public async Task<Result<RetentionPolicyView>> UpdatePolicyAsync(
        Guid policyId,
        int retentionYears,
        RetentionTrigger trigger,
        CancellationToken cancellationToken)
    {
        var policy = await policies.GetAsync(policyId, cancellationToken);
        if (policy is null)
        {
            return Error.NotFound("retention_policy_not_found", $"No retention policy with id {policyId}.");
        }

        var gate = await RequireConfigureAsync(policy.SiteId, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var previous = policy.RetentionYears;

        try
        {
            policy.Update(retentionYears, trigger);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Error.Validation("retention_policy_invalid", ex.Message);
        }

        var documentType = await documentTypes.GetAsync(policy.DocumentTypeId, cancellationToken);

        audit.Record(
            AuditAction.RetentionPolicyChanged, PolicyEntityType, policy.Id,
            documentType?.Code ?? policy.DocumentTypeId.ToString(),
            $"Retention {previous} → {policy.RetentionYears} years, trigger {policy.Trigger}. "
            + "Records already counting down keep their existing expiry.");

        var outcome = await policies.SaveChangesAsync(cancellationToken);
        return outcome.Saved
            ? RetentionPolicyView.From(policy, documentType?.Code ?? "")
            : Error.Conflict("retention_policy_save_conflict", "The policy could not be updated.");
    }

    public async Task<IReadOnlyList<RetentionPolicyView>> ListPoliciesAsync(
        Guid? documentTypeId,
        CancellationToken cancellationToken)
    {
        var found = await policies.ListAsync(documentTypeId, cancellationToken);
        var types = await documentTypes.ListAsync(includeInactive: true, cancellationToken);
        var codes = types.ToDictionary(t => t.Id, t => t.Code);

        return found
            .Select(p => RetentionPolicyView.From(p, codes.GetValueOrDefault(p.DocumentTypeId, "")))
            .ToList();
    }

    /// <summary>
    /// Every caller checks the actor before reaching the code that uses this, so a null here
    /// means a guard was skipped rather than a user being anonymous — which is a bug worth
    /// failing loudly on, not writing "system" into a regulated record over.
    /// </summary>
    private string CurrentActorOrThrow() =>
        currentUser.UserName is { } name && !string.IsNullOrWhiteSpace(name)
            ? name
            : throw new InvalidOperationException("No attributable actor for a retention action.");

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
                $"{Permission.WorkflowConfigure} is required at this scope to configure retention.");
    }
}
