using Dms.Application.Abstractions;
using Dms.Application.Notifications;

namespace Dms.Api.Endpoints;

public static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notifications").WithTags("Notifications");

        group.MapGet("/", async (
            NotificationService service,
            bool? unreadOnly,
            int? limit,
            CancellationToken ct) =>
            (await service.ListMineAsync(unreadOnly ?? false, limit ?? 50, ct)).ToHttpResult());

        group.MapPost("/{id:guid}/read", async (
            NotificationService service,
            Guid id,
            CancellationToken ct) =>
            (await service.MarkReadAsync(id, ct)).ToHttpResult(_ => Results.NoContent()));

        var rules = app.MapGroup("/api/notification-rules").WithTags("Notification Rules");

        // What can be configured, for an admin UI to render the form: every kind, every
        // recipient mode, and the tokens available per kind.
        rules.MapGet("/options", () => Results.Ok(new
        {
            kinds = Enum.GetValues<Dms.Domain.Enums.NotificationKind>()
                .Select(k => new
                {
                    name = k.ToString(),
                    tokens = NotificationRuleService.TokensFor(k),
                }),
            recipientModes = Enum.GetValues<Dms.Domain.Enums.NotificationRecipientMode>()
                .Select(m => m.ToString()),
        }));

        rules.MapGet("/", async (
            NotificationRuleService service,
            Dms.Domain.Enums.NotificationKind? kind,
            CancellationToken ct) =>
            Results.Ok(await service.ListAsync(kind, ct)));

        rules.MapPost("/", async (
            NotificationRuleService service,
            CreateNotificationRuleRequest request,
            CancellationToken ct) =>
        {
            var result = await service.CreateAsync(request, ct);
            return result.ToHttpResult(created =>
                Results.Created($"/api/notification-rules/{created.Id}", created));
        });

        rules.MapPut("/{id:guid}", async (
            NotificationRuleService service,
            Guid id,
            UpdateNotificationRuleRequest request,
            CancellationToken ct) =>
            (await service.UpdateAsync(id, request, ct)).ToHttpResult());

        rules.MapPost("/{id:guid}/enable", async (
            NotificationRuleService service,
            Guid id,
            CancellationToken ct) =>
            (await service.SetEnabledAsync(id, enabled: true, ct)).ToHttpResult());

        rules.MapPost("/{id:guid}/disable", async (
            NotificationRuleService service,
            Guid id,
            CancellationToken ct) =>
            (await service.SetEnabledAsync(id, enabled: false, ct)).ToHttpResult());

        // Renders the templates against sample values so an administrator sees the actual
        // message before saving, rather than discovering a bad token in a 2am sweep.
        rules.MapPost("/preview", (PreviewTemplateRequest request) =>
            NotificationRuleService.Preview(request.Kind, request.SubjectTemplate, request.BodyTemplate)
                .ToHttpResult());

        var jobs = app.MapGroup("/api/jobs").WithTags("Jobs");

        // Evidence the sweep ran, including runs that found nothing. A job that silently stops
        // firing looks the same as one with nothing to report unless empty successes are on
        // record.
        jobs.MapGet("/runs", async (
            IJobRunRepository repository,
            string? jobName,
            int? limit,
            CancellationToken ct) =>
        {
            var runs = await repository.ListAsync(jobName, limit ?? 50, ct);

            return Results.Ok(runs.Select(r => new
            {
                r.Id,
                r.JobName,
                r.Trigger,
                r.StartedAt,
                r.CompletedAt,
                Status = r.Status.ToString(),
                r.ItemsProcessed,
                r.Detail,
            }));
        });

        // Manual trigger, for a first run before the scheduler is switched on or when someone
        // needs today's reminders now. Safe to press repeatedly — the dedupe key means a second
        // run within the same period queues nothing new.
        jobs.MapPost("/reminders/run", async (
            ReminderJob job,
            NotificationService notifications,
            ICurrentUser currentUser,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(currentUser.UserName))
            {
                return Results.BadRequest(new
                {
                    code = "actor_unknown",
                    detail = "The acting user could not be determined.",
                });
            }

            var summary = await job.RunAsync(currentUser.UserName, ct);
            var dispatch = await notifications.DispatchPendingAsync(200, ct);

            return Results.Ok(new
            {
                summary.JobName,
                summary.StartedAt,
                summary.CompletedAt,
                Status = summary.Status.ToString(),
                Queued = summary.ItemsProcessed,
                dispatch.Sent,
                dispatch.Failed,
                summary.Detail,
            });
        });
    }
}
