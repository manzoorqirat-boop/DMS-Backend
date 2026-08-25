using Dms.Application.Abstractions;
using Dms.Application.Documents;
using Dms.Application.Workflows;
using Dms.Application.Common;
using Dms.Domain.Entities;
using Dms.Domain.Enums;

namespace Dms.Application.Signing;

/// <summary>
/// DMS's own review and approval workflow: build a sequential signature route on a draft,
/// collect signatures with password re-authentication at each step, and move the document to
/// Approved once the last step is signed.
/// <para>
/// Replaces the previously planned handoff to ERES/Hastakshar. The regulatory obligations
/// that were ERES's now belong here — §11.50 (printed name, timestamp, meaning displayed with
/// the signature), §11.70 (signature bound to the record it signed), §11.200 (signature
/// credential distinct from the session), §11.300(d) (repeated unauthorised attempts detected).
/// Each is discharged at a specific point below rather than assumed.
/// </para>
/// </summary>
public sealed class ReviewWorkflowService(
    IControlledDocumentRepository documents,
    ISignatureRepository signatures,
    IUserRepository users,
    IDocumentFileStore documentFiles,
    IUnitOfWork unitOfWork,
    ISigningPolicy policy,
    WorkflowDefinitionService workflows,
    DocumentLifecycleService lifecycle,
    RetentionService retention,
    IAuditTrail audit,
    ICurrentUser currentUser)
{
    private const string EntityType = "ControlledDocument";

    /// <summary>
    /// Locks a draft and starts its route.
    /// <para>
    /// The whole route is fixed up front rather than each step naming the next: who is meant
    /// to review a document is a decision made before review starts, and letting a reviewer
    /// choose their own approver at signing time would let the two be the same conversation.
    /// </para>
    /// </summary>
    public async Task<Result<RouteView>> SubmitForReviewAsync(
        Guid documentId,
        SubmitForReviewRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentUser.UserName))
        {
            return Error.Validation(
                "actor_unknown",
                "The acting user could not be determined. Submission must be attributable.");
        }

        if (request.Nominations.Count == 0)
        {
            return Error.Validation("route_empty", "Nominate a signatory for each step of the route.");
        }

        var document = await documents.GetAsync(documentId, cancellationToken);
        if (document is null)
        {
            return Error.NotFound("document_not_found", $"No document with id {documentId}.");
        }

        if (document.Status != DocumentStatus.Draft)
        {
            return Error.Conflict(
                "document_not_submittable",
                $"{document.DocumentNumber} is {document.Status}; only a Draft can be submitted for review.");
        }

        // The route's shape comes from configuration, not from the submitter. They choose who
        // fills each slot; they cannot add a step, drop one, or reorder the chain.
        var templateResult = await workflows.ResolveTemplateAsync(documentId, cancellationToken);
        if (!templateResult.IsSuccess)
        {
            return templateResult.Error!;
        }

        var template = templateResult.Value;

        var nominated = request.Nominations.ToDictionary(n => n.StepOrder, n => n.UserName);

        var missing = template.Slots.Where(slot => !nominated.ContainsKey(slot.StepOrder)).ToList();
        if (missing.Count > 0)
        {
            return Error.Validation(
                "route_incomplete",
                $"No signatory nominated for step(s): {string.Join(", ", missing.Select(m => $"{m.StepOrder} ({m.StepLabel})"))}.");
        }

        var extra = nominated.Keys.Except(template.Slots.Select(s => s.StepOrder)).ToList();
        if (extra.Count > 0)
        {
            return Error.Validation(
                "route_unexpected_step",
                $"Step(s) {string.Join(", ", extra)} are not part of the configured route '{template.WorkflowName}'.");
        }

        // Resolved before anything is written: a route referencing a deactivated account would
        // stall the document at that step with no way forward but an admin intervention.
        var resolved = new List<(RouteSlot Slot, DmsUser User)>();
        foreach (var slot in template.Slots)
        {
            var userName = nominated[slot.StepOrder];

            var candidate = slot.Candidates.FirstOrDefault(c =>
                string.Equals(c.UserName, userName, StringComparison.OrdinalIgnoreCase));

            if (candidate is null)
            {
                // The nomination is checked against the eligible list rather than merely
                // against the user table. Without this, a submitter could name anyone at all
                // and the configured route would be decoration.
                return Error.Validation(
                    "route_candidate_ineligible",
                    $"'{userName}' does not hold {slot.RoleCode} for this document's site and department, "
                    + $"so cannot fill step {slot.StepOrder} ({slot.StepLabel}).");
            }

            var user = await users.GetAsync(candidate.UserId, cancellationToken);
            if (user is null || !user.IsActive)
            {
                return Error.Validation(
                    "route_user_inactive",
                    $"User '{userName}' is deactivated and cannot be placed on a route.");
            }

            resolved.Add((slot, user));
        }

        var distinctUsers = resolved.Select(x => x.User.Id).Distinct().Count();
        if (distinctUsers != resolved.Count)
        {
            // Not a technical constraint — a segregation-of-duties one. One person occupying
            // two steps means the same judgement is being counted twice.
            return Error.Validation(
                "route_duplicate_signatory",
                "The same person cannot occupy more than one step on a route.");
        }

        // Step order comes from the definition, not from the order nominations arrived in.
        foreach (var (slot, user) in resolved)
        {
            signatures.AddRequest(new SignatureRequest(
                document.Id, slot.StepOrder, user.Id, user.UserName, slot.Role, slot.StepLabel));
        }

        document.SubmitForReview();

        audit.Record(
            AuditAction.DocumentSubmittedForReview, EntityType, document.Id, document.DocumentNumber,
            $"Route: {string.Join(" → ", resolved.Select(x => $"{x.User.UserName} ({x.Slot.Role})"))}");
        audit.Record(
            AuditAction.ReviewRouteStarted, EntityType, document.Id, document.DocumentNumber,
            $"{resolved.Count} step(s) under workflow '{template.WorkflowName}' v{template.WorkflowVersion}.");

        var outcome = await documents.SaveChangesAsync(cancellationToken);
        if (!outcome.Saved)
        {
            return Error.Conflict(
                "document_save_conflict",
                "The document could not be submitted because of a conflicting concurrent change.");
        }

        return await BuildRouteViewAsync(document, cancellationToken);
    }

    /// <summary>
    /// Applies a signature to the caller's current step.
    /// <para>
    /// The password check here is not a duplicate of logging in — §11.200(a)(1) requires the
    /// signing credential to be distinct from continued session access, so being logged in is
    /// explicitly not sufficient to sign. A signature that anyone with an unlocked screen
    /// could apply is not attributable to the person it names.
    /// </para>
    /// </summary>
    public async Task<Result<SignatureView>> SignAsync(
        Guid documentId,
        SignRequest request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserName is not { } actorName || string.IsNullOrWhiteSpace(actorName))
        {
            return Error.Validation(
                "actor_unknown",
                "The acting user could not be determined. A signature must be attributable.");
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            return Error.Validation("password_required", "Re-enter your password to sign.");
        }

        if (request.Meaning == SignatureMeaning.Rejected && string.IsNullOrWhiteSpace(request.Reason))
        {
            // A rejection without a stated reason gives the author nothing to act on and gives
            // an auditor no basis for the decision.
            return Error.Validation("reason_required", "A reason is required when rejecting a document.");
        }

        var document = await documents.GetAsync(documentId, cancellationToken);
        if (document is null)
        {
            return Error.NotFound("document_not_found", $"No document with id {documentId}.");
        }

        if (document.Status != DocumentStatus.InReview)
        {
            return Error.Conflict(
                "document_not_in_review",
                $"{document.DocumentNumber} is {document.Status}; there is nothing to sign.");
        }

        var user = await users.GetByUserNameAsync(actorName, cancellationToken);
        if (user is null)
        {
            return Error.NotFound("user_not_found", $"No user '{actorName}'.");
        }

        var now = DateTimeOffset.UtcNow;
        if (!user.CanSign(now))
        {
            return Error.Conflict(
                "signing_blocked",
                user.IsActive
                    ? "Your account is temporarily locked for signing after repeated failed attempts."
                    : "Your account is deactivated.");
        }

        var route = await signatures.GetRouteAsync(documentId, cancellationToken);

        // The lowest-numbered Pending step, and only that one. Signing out of order would let
        // an approval be recorded before the review it's supposed to follow.
        var currentStep = route
            .Where(x => x.Status == SignatureRequestStatus.Pending)
            .OrderBy(x => x.StepOrder)
            .FirstOrDefault();

        if (currentStep is null)
        {
            return Error.Conflict("route_complete", "Every step on this route is already resolved.");
        }

        if (currentStep.UserId != user.Id)
        {
            return Error.Conflict(
                "not_your_step",
                $"Step {currentStep.StepOrder} is assigned to '{currentStep.UserName}', not you.");
        }

        if (!user.VerifyPassword(request.Password))
        {
            user.RegisterFailedSigningAttempt(policy.MaxFailedSigningAttempts, policy.LockoutDuration, now);

            audit.Record(
                AuditAction.SignatureAuthenticationFailed, EntityType, document.Id, document.DocumentNumber,
                $"Step {currentStep.StepOrder} ({user.UserName}); "
                + $"attempt {user.FailedSigningAttempts} of {policy.MaxFailedSigningAttempts}.");

            // Persisted before returning, or the counter resets on every request and the
            // lockout never triggers.
            await users.SaveChangesAsync(cancellationToken);

            return Error.Validation("signature_authentication_failed", "Password is incorrect.");
        }

        var content = await documentFiles.ReadAsync(document.WorkingCopyKey, cancellationToken);
        if (content is null)
        {
            return Error.NotFound(
                "document_file_missing",
                $"The content of {document.DocumentNumber} is missing; it cannot be signed.");
        }

        var contentHash = ContentHasher.Hash(content);

        try
        {
            return await unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                var signature = new ElectronicSignature(
                    document.Id,
                    currentStep.Id,
                    user.Id,
                    user.UserName,
                    user.FullName,
                    user.Department,
                    user.Designation,
                    request.Meaning,
                    contentHash,
                    request.Reason);

                signatures.AddSignature(signature);
                user.RegisterSuccessfulSigning();

                if (request.Meaning == SignatureMeaning.Rejected)
                {
                    currentStep.MarkRejected();

                    // Everything downstream is cancelled, not rejected: those people never saw
                    // the document, and recording a rejection for them would attribute a
                    // decision nobody made.
                    foreach (var later in route.Where(x =>
                                 x.StepOrder > currentStep.StepOrder
                                 && x.Status == SignatureRequestStatus.Pending))
                    {
                        later.Cancel();
                    }

                    document.ReturnForRework();

                    audit.Record(
                        AuditAction.SignatureRejected, EntityType, document.Id, document.DocumentNumber,
                        $"Step {currentStep.StepOrder} by {user.UserName}: {request.Reason}");
                    audit.Record(
                        AuditAction.DocumentReturnedForRework, EntityType, document.Id, document.DocumentNumber,
                        "Returned to Draft; author may edit and resubmit.");
                }
                else
                {
                    currentStep.MarkSigned();

                    audit.Record(
                        AuditAction.SignatureApplied, EntityType, document.Id, document.DocumentNumber,
                        $"Step {currentStep.StepOrder} ({currentStep.StepLabel}) signed by {user.UserName} "
                        + $"as {request.Meaning}; content {contentHash[..16]}…");

                    var remaining = route.Any(x =>
                        x.Id != currentStep.Id && x.Status == SignatureRequestStatus.Pending);

                    if (!remaining)
                    {
                        // Last step signed. Freeze the content as its own immutable object and
                        // record the hash on the document, so the approved artefact and every
                        // signature on it agree on exactly what was approved.
                        var approvedKey = $"approved/{document.Id:N}-r{document.Revision}.docx";
                        await documentFiles.SaveAsync(approvedKey, content, ct);

                        document.MarkApproved(approvedKey, contentHash);

                        audit.Record(
                            AuditAction.DocumentApproved, EntityType, document.Id, document.DocumentNumber,
                            $"All {route.Count} step(s) signed. Approved content {contentHash[..16]}…");
                    }
                }

                var outcome = await signatures.SaveChangesAsync(ct);
                if (!outcome.Saved)
                {
                    throw new DraftAbortedException(Error.Conflict(
                        "signature_save_conflict",
                        "The signature could not be recorded because of a conflicting concurrent change."));
                }

                return SignatureView.From(signature, currentStep);
            }, cancellationToken);
        }
        catch (DraftAbortedException ex)
        {
            return ex.Error;
        }
    }

    /// <summary>The current route with its signatures — what a reviewer sees before signing.</summary>
    public async Task<Result<RouteView>> GetRouteAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await documents.GetAsync(documentId, cancellationToken);
        return document is null
            ? Error.NotFound("document_not_found", $"No document with id {documentId}.")
            : await BuildRouteViewAsync(document, cancellationToken);
    }

    /// <summary>The caller's signing queue.</summary>
    public async Task<Result<IReadOnlyList<PendingSignatureView>>> GetMyPendingAsync(
        CancellationToken cancellationToken)
    {
        if (currentUser.UserName is not { } actorName || string.IsNullOrWhiteSpace(actorName))
        {
            return Error.Validation("actor_unknown", "The acting user could not be determined.");
        }

        var user = await users.GetByUserNameAsync(actorName, cancellationToken);
        if (user is null)
        {
            return Error.NotFound("user_not_found", $"No user '{actorName}'.");
        }

        var pending = await signatures.GetPendingForUserAsync(user.Id, cancellationToken);

        var views = new List<PendingSignatureView>();
        foreach (var step in pending)
        {
            var document = await documents.GetAsync(step.DocumentId, cancellationToken);
            if (document is null || document.Status != DocumentStatus.InReview)
            {
                continue;
            }

            // Only surface a step that is actually actionable now. Showing a step that's
            // waiting behind an earlier one produces a queue full of things the person can't
            // do, which trains people to ignore the queue.
            var route = await signatures.GetRouteAsync(step.DocumentId, cancellationToken);
            var isCurrent = route
                .Where(x => x.Status == SignatureRequestStatus.Pending)
                .OrderBy(x => x.StepOrder)
                .FirstOrDefault()?.Id == step.Id;

            if (isCurrent)
            {
                views.Add(new PendingSignatureView(
                    document.Id, document.DocumentNumber, document.Title,
                    step.StepOrder, step.StepLabel, step.Role, document.SubmittedAt));
            }
        }

        return views;
    }

    /// <summary>
    /// Issues an approved document with an effective date, and records the issuance as a
    /// dated act in its own right.
    /// </summary>
    public async Task<Result<DocumentIssueView>> MakeEffectiveAsync(
        Guid documentId,
        DateOnly effectiveDate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentUser.UserName))
        {
            return Error.Validation("actor_unknown", "The acting user could not be determined.");
        }

        var document = await documents.GetAsync(documentId, cancellationToken);
        if (document is null)
        {
            return Error.NotFound("document_not_found", $"No document with id {documentId}.");
        }

        // The predecessor, if this is a revision. Resolved before anything changes so that
        // standing it down and promoting the successor flush together — a family left with two
        // current revisions, or none, would leave "which version do I follow" unanswerable.
        var predecessor = await documents.GetCurrentRevisionAsync(document.FamilyId, cancellationToken);

        // Computed from the type's review policy at the moment of issuance, so a later policy
        // change doesn't silently move the due date of something already in force.
        var nextReviewDate = await lifecycle.ResolveNextReviewDateAsync(
            document.DocumentTypeId, document.SiteId, effectiveDate, cancellationToken);

        try
        {
            document.MakeEffective(effectiveDate, DateOnly.FromDateTime(DateTime.UtcNow), nextReviewDate);

            if (predecessor is not null && predecessor.Id != document.Id)
            {
                predecessor.Supersede();
                predecessor.StandDown();
            }

            document.PromoteToCurrent();
        }
        catch (InvalidOperationException ex)
        {
            return Error.Conflict("document_not_issuable", ex.Message);
        }

        audit.Record(
            AuditAction.DocumentMadeEffective, EntityType, document.Id, document.DocumentNumber,
            $"Rev {document.Revision:00} effective {effectiveDate:yyyy-MM-dd}."
            + (nextReviewDate is { } due ? $" Next review due {due:yyyy-MM-dd}." : " No review policy applies."));

        if (predecessor is not null && predecessor.Id != document.Id)
        {
            await retention.StartRetentionAsync(predecessor, RetentionTrigger.Superseded, cancellationToken);

            audit.Record(
                AuditAction.DocumentSuperseded, EntityType, predecessor.Id, predecessor.DocumentNumber,
                $"Rev {predecessor.Revision:00} superseded by Rev {document.Revision:00}.");
        }

        var outcome = await documents.SaveChangesAsync(cancellationToken);
        if (!outcome.Saved && outcome.ViolatedIndexContains("one_current_per_family"))
        {
            return Error.Conflict(
                "revision_issue_conflict",
                "Another revision of this document was issued concurrently. Reload and retry.");
        }

        return outcome.Saved
            ? new DocumentIssueView(document.DocumentNumber, document.Status, document.EffectiveDate)
            : Error.Conflict(
                "document_save_conflict",
                "The document could not be issued because of a conflicting concurrent change.");
    }

    private async Task<RouteView> BuildRouteViewAsync(
        ControlledDocument document,
        CancellationToken cancellationToken)
    {
        var route = await signatures.GetRouteAsync(document.Id, cancellationToken);
        var applied = await signatures.GetSignaturesAsync(document.Id, cancellationToken);

        var byRequest = applied.ToDictionary(x => x.SignatureRequestId);

        var steps = route
            .OrderBy(x => x.StepOrder)
            .Select(step => new RouteStepView(
                step.StepOrder,
                step.StepLabel,
                step.UserName,
                step.Role,
                step.Status,
                byRequest.TryGetValue(step.Id, out var signature)
                    ? SignatureView.From(signature, step)
                    : null))
            .ToList();

        return new RouteView(
            document.Id,
            document.DocumentNumber,
            document.Status,
            document.ApprovedContentHash,
            steps);
    }
}
