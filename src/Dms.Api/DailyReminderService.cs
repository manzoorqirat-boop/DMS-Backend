using Dms.Application.Notifications;

namespace Dms.Api;

/// <summary>
/// Runs the reminder sweep and drains the notification queue on a fixed interval.
/// <para>
/// A hosted service rather than an external scheduler, because DMS deploys as a single
/// application and adding a cron dependency for one daily job would be a deployment
/// requirement to get wrong. The trade-off is that every running instance has its own timer —
/// handled by the dedupe key and its unique index rather than by leader election, which is the
/// simpler mechanism and the one that also protects against a manual trigger racing the
/// scheduled run.
/// </para>
/// <para>
/// Disabled by default. A background job that silently starts mailing people the first time
/// someone runs the API locally is a bad surprise; it has to be switched on deliberately.
/// </para>
/// </summary>
public sealed class DailyReminderService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<DailyReminderService> logger)
    : BackgroundService
{
    public const string EnabledKey = "Scheduler:Enabled";
    public const string IntervalHoursKey = "Scheduler:IntervalHours";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue(EnabledKey, false))
        {
            logger.LogInformation(
                "Reminder scheduler is disabled. Set {Key} to true to enable it.", EnabledKey);
            return;
        }

        var interval = TimeSpan.FromHours(Math.Clamp(configuration.GetValue(IntervalHoursKey, 24), 1, 168));

        logger.LogInformation("Reminder scheduler started; interval {Interval}.", interval);

        // Staggered so several instances starting together don't all sweep in the same instant.
        // They'd deduplicate correctly, but they'd also all do the work.
        await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(10, 90)), stoppingToken);

        using var timer = new PeriodicTimer(interval);

        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Its own scope: the DbContext is scoped, and a hosted service has no ambient one.
            using var scope = scopeFactory.CreateScope();

            var job = scope.ServiceProvider.GetRequiredService<ReminderJob>();
            var notifications = scope.ServiceProvider.GetRequiredService<NotificationService>();

            var summary = await job.RunAsync("Scheduled", cancellationToken);
            var dispatch = await notifications.DispatchPendingAsync(200, cancellationToken);

            logger.LogInformation(
                "Reminder sweep {Status}: {Queued} queued, {Sent} sent, {Failed} failed.",
                summary.Status, summary.ItemsProcessed, dispatch.Sent, dispatch.Failed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down. Not an error.
        }
        catch (Exception ex)
        {
            // Swallowed on purpose: an unhandled exception here would kill the hosted service
            // and stop every future sweep, turning one bad night into permanent silence.
            logger.LogError(ex, "Reminder sweep failed. The next scheduled run will still occur.");
        }
    }
}
