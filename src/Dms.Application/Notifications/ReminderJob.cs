using System.Globalization;
using Dms.Application.Abstractions;
using Dms.Application.Common;
using Dms.Domain.Entities;
using Dms.Domain.Enums;

namespace Dms.Application.Notifications;

/// <summary>
/// The daily reminder sweep: warns before review dates fall due, chases overdue ones, nudges
/// pending signatures, and flags copies still owed back.
/// <para>
/// Every notification carries a dedupe key scoped to the day, so running this twice — by the
/// scheduler and by someone pressing the manual trigger, or by two application instances at
/// once — queues each reminder once. That matters more than it sounds: a reminder system that
/// double-sends gets muted by its recipients within a week, and a muted reminder system is the
/// same as none.
/// </para>
/// </summary>
public sealed class ReminderJob(
    IControlledDocumentRepository documents,
    IDistributionRepository distributions,
    ISignatureRepository signatures,
    IUserRepository users,
    INotificationRepository notifications,
    IJobRunRepository jobRuns,
    IAuditTrail audit,
    IClock clock)
{
    public const string JobName = "daily-reminders";

    /// <summary>
    /// How far ahead review reminders look when a document's type has no pre-intimation
    /// configured. Thirty days is enough to draft and route a revision without being so far out
    /// that the warning is noise.
    /// </summary>
    private const int DefaultPreIntimationDays = 30;

    /// <summary>Days a controlled copy may sit unacknowledged before it is chased.</summary>
    private const int UnacknowledgedAfterDays = 7;

    public async Task<JobRunSummary> RunAsync(string trigger, CancellationToken cancellationToken)
    {
        var run = new ScheduledJobRun(JobName, clock.UtcNow, trigger);
        var errors = new List<string>();
        var queued = 0;

        // Each section is independently guarded. A failure gathering signature reminders must
        // not cost the review reminders — a partial sweep beats an abandoned one, provided the
        // run record says which part failed.
        queued += await SafelyAsync(
            () => QueueReviewRemindersAsync(cancellationToken), "review reminders", errors);
        queued += await SafelyAsync(
            () => QueuePendingSignatureRemindersAsync(cancellationToken), "signature reminders", errors);
        queued += await SafelyAsync(
            () => QueueUnacknowledgedCopyRemindersAsync(cancellationToken), "copy acknowledgement reminders", errors);
        queued += await SafelyAsync(
            () => QueueRetrievalRemindersAsync(cancellationToken), "retrieval reminders", errors);

        var status = errors.Count == 0 ? JobRunStatus.Succeeded : JobRunStatus.CompletedWithErrors;
        var detail = errors.Count == 0
            ? $"{queued} notification(s) queued."
            : $"{queued} notification(s) queued. Errors: {string.Join(" | ", errors)}";

        run.Complete(status, queued, detail);
        jobRuns.Add(run);

        // Recorded even on an empty run. A job that silently stops firing is indistinguishable
        // from a job with nothing to report unless the empty successes are on record too.
        audit.Record(AuditAction.ScheduledJobRan, "ScheduledJobRun", run.Id, JobName, detail);

        await jobRuns.SaveChangesAsync(cancellationToken);

        return new JobRunSummary(JobName, run.StartedAt, run.CompletedAt, status, queued, detail);
    }

    /// <summary>
    /// Documents approaching or past their review date. Both are queued from one pass because
    /// they come from the same query — the kind differs only by whether the date has passed.
    /// </summary>
    private async Task<int> QueueReviewRemindersAsync(CancellationToken cancellationToken)
    {
        var today = clock.Today;
        var horizon = today.AddDays(DefaultPreIntimationDays);

        var due = await documents.ListDueForReviewAsync(horizon, null, null, cancellationToken);
        if (due.Count == 0)
        {
            return 0;
        }

        var candidates = new List<PendingNotification>();

        foreach (var document in due)
        {
            // Addressed to the author. A department-owner concept would be better and doesn't
            // exist yet; the author is the closest attributable person and is at least
            // guaranteed to be a real user on the record.
            var recipient = await users.GetByUserNameAsync(document.Author, cancellationToken);
            if (recipient is null || !recipient.IsActive)
            {
                continue;
            }

            var overdue = document.NextReviewDate!.Value < today;
            var kind = overdue ? NotificationKind.ReviewOverdue : NotificationKind.ReviewComingDue;
            var days = document.NextReviewDate!.Value.DayNumber - today.DayNumber;

            var subject = overdue
                ? $"Overdue for review: {document.DocumentNumber}"
                : $"Review due in {days} day(s): {document.DocumentNumber}";

            var body = overdue
                ? $"{document.DocumentNumber} Rev {document.Revision:00} — \"{document.Title}\" was due for "
                  + $"periodic review on {document.NextReviewDate:yyyy-MM-dd}, {-days} day(s) ago. "
                  + "It remains effective and in use until reviewed or revised."
                : $"{document.DocumentNumber} Rev {document.Revision:00} — \"{document.Title}\" is due for "
                  + $"periodic review on {document.NextReviewDate:yyyy-MM-dd}. "
                  + "Record a review, or start a revision if changes are needed.";

            candidates.Add(new PendingNotification(
                recipient, kind, subject, body,
                // Overdue reminders repeat daily; coming-due ones are keyed to the due date so
                // they queue once rather than every day of the pre-intimation window.
                overdue
                    ? DedupeKey(kind, document.Id, recipient.Id, today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                    : DedupeKey(kind, document.Id, recipient.Id, document.NextReviewDate!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                document.Id));
        }

        return await QueueAsync(candidates, cancellationToken);
    }

    private async Task<int> QueuePendingSignatureRemindersAsync(CancellationToken cancellationToken)
    {
        var pending = await signatures.ListPendingForAllAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return 0;
        }

        var today = clock.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var candidates = new List<PendingNotification>();

        foreach (var request in pending)
        {
            var recipient = await users.GetAsync(request.UserId, cancellationToken);
            if (recipient is null || !recipient.IsActive)
            {
                continue;
            }

            var document = await documents.GetAsync(request.DocumentId, cancellationToken);
            if (document is null)
            {
                continue;
            }

            candidates.Add(new PendingNotification(
                recipient,
                NotificationKind.SignaturePending,
                $"Signature required: {document.DocumentNumber}",
                $"{document.DocumentNumber} Rev {document.Revision:00} — \"{document.Title}\" is waiting on your "
                + $"signature at step {request.StepOrder} ({request.StepLabel}).",
                DedupeKey(NotificationKind.SignaturePending, request.Id, recipient.Id, today),
                document.Id));
        }

        return await QueueAsync(candidates, cancellationToken);
    }

    private async Task<int> QueueUnacknowledgedCopyRemindersAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow.AddDays(-UnacknowledgedAfterDays);
        var today = clock.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var stale = await distributions.ListUnacknowledgedBeforeAsync(cutoff, cancellationToken);
        if (stale.Count == 0)
        {
            return 0;
        }

        var candidates = new List<PendingNotification>();

        foreach (var copy in stale)
        {
            // Chased back to whoever issued it, not to the recipient: the recipient may not be
            // a system user at all, and the issuer is the person accountable for the copy
            // reaching them.
            var issuer = await users.GetByUserNameAsync(copy.IssuedBy, cancellationToken);
            if (issuer is null || !issuer.IsActive)
            {
                continue;
            }

            var document = await documents.GetAsync(copy.DocumentId, cancellationToken);
            if (document is null)
            {
                continue;
            }

            candidates.Add(new PendingNotification(
                issuer,
                NotificationKind.CopyUnacknowledged,
                $"Copy {copy.CopyNumber} not acknowledged: {document.DocumentNumber}",
                $"{copy.CopyType} copy {copy.CopyNumber} of {document.DocumentNumber} was issued to "
                + $"{copy.IssuedToName} on {copy.CreatedAt:yyyy-MM-dd} and has not been acknowledged.",
                DedupeKey(NotificationKind.CopyUnacknowledged, copy.Id, issuer.Id, today),
                document.Id));
        }

        return await QueueAsync(candidates, cancellationToken);
    }

    private async Task<int> QueueRetrievalRemindersAsync(CancellationToken cancellationToken)
    {
        var outstanding = await distributions.ListPendingRetrievalAsync(null, cancellationToken);
        if (outstanding.Count == 0)
        {
            return 0;
        }

        var today = clock.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var candidates = new List<PendingNotification>();

        foreach (var (copy, document) in outstanding)
        {
            var issuer = await users.GetByUserNameAsync(copy.IssuedBy, cancellationToken);
            if (issuer is null || !issuer.IsActive)
            {
                continue;
            }

            candidates.Add(new PendingNotification(
                issuer,
                NotificationKind.CopyRetrievalRequired,
                $"Retrieve copy {copy.CopyNumber}: {document.DocumentNumber}",
                $"{document.DocumentNumber} Rev {document.Revision:00} is {document.Status}, but "
                + $"{copy.CopyType} copy {copy.CopyNumber} is still held by {copy.IssuedToName}. "
                + "Collect it, or record it as destroyed or lost.",
                DedupeKey(NotificationKind.CopyRetrievalRequired, copy.Id, issuer.Id, today),
                document.Id));
        }

        return await QueueAsync(candidates, cancellationToken);
    }

    /// <summary>
    /// Filters out anything already queued under the same key, then persists the rest. The
    /// bulk key check keeps a sweep to one round trip instead of one per candidate; the unique
    /// index on the column is what actually guarantees uniqueness when two instances race.
    /// </summary>
    private async Task<int> QueueAsync(
        IReadOnlyList<PendingNotification> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return 0;
        }

        var keys = candidates.Select(c => c.DedupeKey).Distinct().ToList();
        var existing = (await notifications.FindExistingDedupeKeysAsync(keys, cancellationToken)).ToHashSet(StringComparer.Ordinal);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queued = 0;

        foreach (var candidate in candidates)
        {
            if (existing.Contains(candidate.DedupeKey) || !seen.Add(candidate.DedupeKey))
            {
                continue;
            }

            notifications.Add(new Notification(
                candidate.Recipient.Id,
                candidate.Recipient.UserName,
                candidate.Recipient.Email,
                candidate.Kind,
                candidate.Subject,
                candidate.Body,
                candidate.DedupeKey,
                candidate.SubjectDocumentId));

            queued++;
        }

        if (queued > 0)
        {
            await notifications.SaveChangesAsync(cancellationToken);
        }

        return queued;
    }

    private static string DedupeKey(NotificationKind kind, Guid subjectId, Guid recipientId, string period) =>
        $"{kind}:{subjectId:N}:{recipientId:N}:{period}";

    private static async Task<int> SafelyAsync(
        Func<Task<int>> section,
        string label,
        List<string> errors)
    {
        try
        {
            return await section();
        }
        catch (Exception ex)
        {
            errors.Add($"{label}: {ex.Message}");
            return 0;
        }
    }

    private sealed record PendingNotification(
        DmsUser Recipient,
        NotificationKind Kind,
        string Subject,
        string Body,
        string DedupeKey,
        Guid? SubjectDocumentId);
}

public sealed record JobRunSummary(
    string JobName,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    JobRunStatus Status,
    int ItemsProcessed,
    string? Detail);
