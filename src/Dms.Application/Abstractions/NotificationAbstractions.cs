using Dms.Application.Common;
using Dms.Domain.Entities;
using Dms.Domain.Enums;

namespace Dms.Application.Abstractions;

public interface INotificationRepository
{
    Task<Notification?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Notification>> ListForUserAsync(
        Guid userId,
        bool unreadOnly,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>Queued but not yet handed to a sender, oldest first.</summary>
    Task<IReadOnlyList<Notification>> ListPendingAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Dedupe keys already present from the supplied set. Checked in bulk before queuing so a
    /// reminder run does one round trip rather than one per candidate.
    /// </summary>
    Task<IReadOnlyList<string>> FindExistingDedupeKeysAsync(
        IReadOnlyList<string> dedupeKeys,
        CancellationToken cancellationToken);

    void Add(Notification notification);

    Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IJobRunRepository
{
    Task<IReadOnlyList<ScheduledJobRun>> ListAsync(string? jobName, int limit, CancellationToken cancellationToken);

    void Add(ScheduledJobRun run);

    Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Delivers a queued notification.
/// <para>
/// An interface because SMTP configuration is deployment-specific and not decided yet. The
/// queue, the dedupe and the run record are real regardless of whether anything is actually
/// mailed — so switching on real delivery later is a registration change, not a redesign.
/// </para>
/// </summary>
public interface INotificationSender
{
    /// <summary>
    /// Returns whether delivery succeeded, and why not when it didn't. Deliberately not
    /// throwing: one bad address must not abandon the rest of the batch.
    /// </summary>
    Task<NotificationDeliveryResult> SendAsync(Notification notification, CancellationToken cancellationToken);
}

public sealed record NotificationDeliveryResult(bool Delivered, string? FailureReason)
{
    public static readonly NotificationDeliveryResult Success = new(true, null);

    public static NotificationDeliveryResult Failed(string reason) => new(false, reason);
}

/// <summary>
/// Wall-clock access, injected rather than called statically so reminder windows can be tested
/// without waiting for a real date to arrive.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    DateOnly Today => DateOnly.FromDateTime(UtcNow.UtcDateTime);
}
