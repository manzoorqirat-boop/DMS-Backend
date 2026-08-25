using Dms.Application.Abstractions;
using Dms.Application.Common;
using Dms.Domain.Entities;
using Dms.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dms.Infrastructure.Persistence.Repositories;

public sealed class NotificationRepository(DmsDbContext db) : INotificationRepository
{
    public Task<Notification?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Notifications.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Notification>> ListForUserAsync(
        Guid userId,
        bool unreadOnly,
        int limit,
        CancellationToken cancellationToken)
    {
        var query = db.Notifications.AsNoTracking().Where(x => x.RecipientUserId == userId);

        if (unreadOnly)
        {
            query = query.Where(x => x.ReadAt == null);
        }

        return await query
            .OrderByDescending(x => x.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Notification>> ListPendingAsync(
        int limit,
        CancellationToken cancellationToken) =>
        await db.Notifications
            .Where(x => x.Status == NotificationStatus.Pending)
            .OrderBy(x => x.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<string>> FindExistingDedupeKeysAsync(
        IReadOnlyList<string> dedupeKeys,
        CancellationToken cancellationToken) =>
        await db.Notifications
            .AsNoTracking()
            .Where(x => dedupeKeys.Contains(x.DedupeKey))
            .Select(x => x.DedupeKey)
            .ToListAsync(cancellationToken);

    public void Add(Notification notification) => db.Notifications.Add(notification);

    public Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken) =>
        SaveChangesTranslator.SaveAsync(db, cancellationToken);
}

public sealed class JobRunRepository(DmsDbContext db) : IJobRunRepository
{
    public async Task<IReadOnlyList<ScheduledJobRun>> ListAsync(
        string? jobName,
        int limit,
        CancellationToken cancellationToken)
    {
        var query = db.ScheduledJobRuns.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(jobName))
        {
            query = query.Where(x => x.JobName == jobName);
        }

        return await query
            .OrderByDescending(x => x.StartedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken);
    }

    public void Add(ScheduledJobRun run) => db.ScheduledJobRuns.Add(run);

    public Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken) =>
        SaveChangesTranslator.SaveAsync(db, cancellationToken);
}

/// <summary>Wall clock. Trivial, and injected so reminder windows are testable.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// Stand-in sender that records delivery to the application log instead of mailing.
/// <para>
/// SMTP configuration is deployment-specific and not decided yet. This keeps the queue
/// draining so notifications don't pile up as Pending forever, while making it obvious in the
/// logs that nothing was actually sent. Replace before go-live — a reminder system that
/// reports success without delivering is worse than one that visibly fails.
/// </para>
/// </summary>
public sealed class LoggingNotificationSender(ILogger<LoggingNotificationSender> logger) : INotificationSender
{
    public Task<NotificationDeliveryResult> SendAsync(
        Notification notification,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "NOT SENT (no mail transport configured) — {Kind} for {User} <{Email}>: {Subject}",
            notification.Kind,
            notification.RecipientUserName,
            notification.RecipientEmail ?? "no address",
            notification.Subject);

        return Task.FromResult(NotificationDeliveryResult.Success);
    }
}

public sealed class EditingSessionRepository(DmsDbContext db) : IEditingSessionRepository
{
    public Task<EditingSession?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.EditingSessions.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<EditingSession?> GetActiveForDocumentAsync(
        Guid documentId,
        CancellationToken cancellationToken) =>
        db.EditingSessions.FirstOrDefaultAsync(
            x => x.DocumentId == documentId && x.Status == EditingSessionStatus.Active,
            cancellationToken);

    public async Task<IReadOnlyList<EditingSession>> ListForDocumentAsync(
        Guid documentId,
        CancellationToken cancellationToken) =>
        await db.EditingSessions
            .AsNoTracking()
            .Where(x => x.DocumentId == documentId)
            .OrderByDescending(x => x.StartedAt)
            .ToListAsync(cancellationToken);

    public void Add(EditingSession session) => db.EditingSessions.Add(session);

    public Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken) =>
        SaveChangesTranslator.SaveAsync(db, cancellationToken);
}
