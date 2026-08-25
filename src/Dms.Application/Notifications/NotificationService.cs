using Dms.Application.Abstractions;
using Dms.Application.Common;
using Dms.Domain.Entities;
using Dms.Domain.Enums;

namespace Dms.Application.Notifications;

/// <summary>
/// Reads a user's notifications and dispatches the queue.
/// <para>
/// Dispatch is separate from queuing on purpose: the reminder sweep writes rows and returns,
/// and delivery happens afterwards. A sweep that blocked on SMTP would take as long as the
/// slowest mail server and lose everything after the first timeout.
/// </para>
/// </summary>
public sealed class NotificationService(
    INotificationRepository notifications,
    IUserRepository users,
    INotificationSender sender,
    ICurrentUser currentUser)
{
    /// <summary>
    /// Attempts delivery of queued notifications, up to <paramref name="batchSize"/>.
    /// <para>
    /// A failure marks that one row Failed and moves on. One unreachable address must not
    /// stall the queue behind it, and a permanently-retried failure would do exactly that.
    /// </para>
    /// </summary>
    public async Task<DispatchSummary> DispatchPendingAsync(
        int batchSize,
        CancellationToken cancellationToken)
    {
        var pending = await notifications.ListPendingAsync(
            Math.Clamp(batchSize, 1, 500), cancellationToken);

        if (pending.Count == 0)
        {
            return new DispatchSummary(0, 0, 0);
        }

        var sent = 0;
        var failed = 0;

        foreach (var notification in pending)
        {
            NotificationDeliveryResult result;
            try
            {
                result = await sender.SendAsync(notification, cancellationToken);
            }
            catch (Exception ex)
            {
                // A sender that throws rather than returning a failure is still just one
                // failed notification, not a failed batch.
                result = NotificationDeliveryResult.Failed(ex.Message);
            }

            if (result.Delivered)
            {
                notification.MarkSent();
                sent++;
            }
            else
            {
                notification.MarkFailed(result.FailureReason ?? "Unknown delivery failure.");
                failed++;
            }
        }

        await notifications.SaveChangesAsync(cancellationToken);

        return new DispatchSummary(pending.Count, sent, failed);
    }

    public async Task<Result<IReadOnlyList<NotificationView>>> ListMineAsync(
        bool unreadOnly,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentUser.UserName))
        {
            return Error.Validation("actor_unknown", "The acting user could not be determined.");
        }

        var user = await users.GetByUserNameAsync(currentUser.UserName, cancellationToken);
        if (user is null)
        {
            return Error.NotFound("user_not_found", "The acting user has no DMS account.");
        }

        var found = await notifications.ListForUserAsync(
            user.Id, unreadOnly, Math.Clamp(limit, 1, 200), cancellationToken);

        return Result<IReadOnlyList<NotificationView>>.Success(
            found.Select(NotificationView.From).ToList());
    }

    /// <summary>
    /// Marks one of the caller's own notifications read. Scoped to the caller deliberately —
    /// there is no legitimate reason to mark someone else's reminder as seen.
    /// </summary>
    public async Task<Result<bool>> MarkReadAsync(Guid notificationId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentUser.UserName))
        {
            return Error.Validation("actor_unknown", "The acting user could not be determined.");
        }

        var user = await users.GetByUserNameAsync(currentUser.UserName, cancellationToken);
        if (user is null)
        {
            return Error.NotFound("user_not_found", "The acting user has no DMS account.");
        }

        var notification = await notifications.GetAsync(notificationId, cancellationToken);
        if (notification is null || notification.RecipientUserId != user.Id)
        {
            // Same response for "doesn't exist" and "isn't yours", so the endpoint can't be
            // used to probe which notification ids are real.
            return Error.NotFound("notification_not_found", $"No notification with id {notificationId}.");
        }

        notification.MarkRead();

        var outcome = await notifications.SaveChangesAsync(cancellationToken);
        return outcome.Saved
            ? Result<bool>.Success(true)
            : Error.Conflict("notification_save_conflict", "The notification could not be updated.");
    }
}

public sealed record DispatchSummary(int Attempted, int Sent, int Failed);

public sealed record NotificationView(
    Guid Id,
    NotificationKind Kind,
    string Subject,
    string Body,
    NotificationStatus Status,
    Guid? SubjectDocumentId,
    bool IsRead,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt)
{
    public static NotificationView From(Notification notification) => new(
        notification.Id,
        notification.Kind,
        notification.Subject,
        notification.Body,
        notification.Status,
        notification.SubjectDocumentId,
        notification.ReadAt is not null,
        notification.CreatedAt,
        notification.SentAt);
}
