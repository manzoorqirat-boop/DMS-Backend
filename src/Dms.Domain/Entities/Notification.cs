using Dms.Domain.Common;
using Dms.Domain.Enums;

namespace Dms.Domain.Entities;

/// <summary>
/// One notification raised for one user.
/// <para>
/// Queued to the database rather than sent inline. A reminder job that mails directly has no
/// record of what it sent, retries nothing, and duplicates everything when it runs twice —
/// and "did the system actually warn anyone before that SOP went overdue" is a question the
/// records have to answer.
/// </para>
/// </summary>
public class Notification : Entity
{
    private Notification() { }

    public Notification(
        Guid recipientUserId,
        string recipientUserName,
        string? recipientEmail,
        NotificationKind kind,
        string subject,
        string body,
        string dedupeKey,
        Guid? subjectDocumentId)
    {
        RecipientUserId = recipientUserId;
        RecipientUserName = RequireNonEmpty(recipientUserName, nameof(recipientUserName));
        RecipientEmail = string.IsNullOrWhiteSpace(recipientEmail) ? null : recipientEmail.Trim();
        Kind = kind;
        Subject = RequireNonEmpty(subject, nameof(subject));
        Body = RequireNonEmpty(body, nameof(body));
        DedupeKey = RequireNonEmpty(dedupeKey, nameof(dedupeKey));
        SubjectDocumentId = subjectDocumentId;
        Status = NotificationStatus.Pending;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid RecipientUserId { get; private set; }

    public string RecipientUserName { get; private set; } = "";

    /// <summary>
    /// Null when the user has no address on file. The notification is still queued and still
    /// readable in-app — dropping it because there's nowhere to mail it would make the reminder
    /// silently not exist for exactly the users least likely to notice.
    /// </summary>
    public string? RecipientEmail { get; private set; }

    public NotificationKind Kind { get; private set; }

    public string Subject { get; private set; } = "";

    public string Body { get; private set; } = "";

    /// <summary>
    /// Stable identity for "this reminder, about this thing, for this person, in this period".
    /// A unique index on it is what makes the daily job idempotent — including when two
    /// application instances run it at the same moment, which a scheduler inside a
    /// horizontally-scaled app will eventually do.
    /// </summary>
    public string DedupeKey { get; private set; } = "";

    /// <summary>The document this concerns, when there is one. Lets a user's list link through.</summary>
    public Guid? SubjectDocumentId { get; private set; }

    public NotificationStatus Status { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    /// <summary>Why delivery failed. Retained rather than cleared on a later attempt.</summary>
    public string? FailureReason { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset? ReadAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public void MarkSent()
    {
        AttemptCount++;
        SentAt = DateTimeOffset.UtcNow;
        FailureReason = null;
        Status = NotificationStatus.Sent;
    }

    public void MarkFailed(string reason)
    {
        AttemptCount++;
        FailureReason = string.IsNullOrWhiteSpace(reason) ? "Unspecified delivery failure." : reason.Trim();
        Status = NotificationStatus.Failed;
    }

    /// <summary>
    /// Marking read is idempotent — the first read is the one that matters, and re-reading
    /// shouldn't move the timestamp of when the person was actually informed.
    /// </summary>
    public void MarkRead()
    {
        ReadAt ??= DateTimeOffset.UtcNow;
    }

    private static string RequireNonEmpty(string value, string paramName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{paramName} is required.", paramName)
            : value.Trim();
}

/// <summary>
/// Evidence that a scheduled job ran, and what it did.
/// <para>
/// Recorded for every run, including runs that found nothing to do. A job that silently stops
/// firing looks identical to a job with nothing to report unless the successful empty runs are
/// on record — and by the time anyone notices, the gap in the reminders has already happened.
/// </para>
/// </summary>
public class ScheduledJobRun : Entity
{
    private ScheduledJobRun() { }

    public ScheduledJobRun(string jobName, DateTimeOffset startedAt, string trigger)
    {
        JobName = string.IsNullOrWhiteSpace(jobName)
            ? throw new ArgumentException("Job name is required.", nameof(jobName))
            : jobName.Trim();
        StartedAt = startedAt;
        Trigger = trigger;
        Status = JobRunStatus.Succeeded;
    }

    public string JobName { get; private set; } = "";

    /// <summary>"Scheduled" or the username who triggered it manually.</summary>
    public string Trigger { get; private set; } = "";

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public JobRunStatus Status { get; private set; }

    /// <summary>How many notifications this run queued. Zero is a valid, meaningful result.</summary>
    public int ItemsProcessed { get; private set; }

    /// <summary>Summary of what happened, including any errors encountered.</summary>
    public string? Detail { get; private set; }

    public void Complete(JobRunStatus status, int itemsProcessed, string? detail)
    {
        Status = status;
        ItemsProcessed = itemsProcessed;
        Detail = detail;
        CompletedAt = DateTimeOffset.UtcNow;
    }
}
