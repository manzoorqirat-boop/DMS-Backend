using System.Globalization;
using Dms.Application.Abstractions;
using Dms.Domain.Entities;
using Dms.Domain.Enums;
using Dms.Domain.Services;

namespace Dms.Application.Notifications;

/// <summary>
/// The daily reminder sweep. Entirely driven by <see cref="NotificationRule"/> master data —
/// which reminders exist, how far ahead they warn, how often they repeat, who receives them
/// and what they say are all configuration.
/// <para>
/// A kind with no enabled rule simply doesn't fire. That is a deliberate default: a system
/// that invented reminders nobody configured would fill inboxes with items whose owners never
/// agreed they were owners, and the fastest way to make a reminder system useless is to make
/// it noisy.
/// </para>
/// <para>
/// Every notification carries a dedupe key scoped to the rule's repeat window, so running this
/// twice — by the scheduler and by a manual trigger, or by two instances at once — queues each
/// reminder once.
/// </para>
/// </summary>
public sealed class ReminderJob(
    IControlledDocumentRepository documents,
    IDistributionRepository distributions,
    ISignatureRepository signatures,
    IUserRepository users,
    IRoleRepository roles,
    ISiteRepository sites,
    IDepartmentRepository departments,
    INotificationRepository notifications,
    INotificationRuleRepository rules,
    IJobRunRepository jobRuns,
    IAuditTrail audit,
    IClock clock)
{
    public const string JobName = "daily-reminders";

    public async Task<JobRunSummary> RunAsync(string trigger, CancellationToken cancellationToken)
    {
        var run = new ScheduledJobRun(JobName, clock.UtcNow, trigger);
        var errors = new List<string>();
        var queued = 0;

        // Each section is independently guarded. A failure gathering signature reminders must
        // not cost the review reminders — a partial sweep beats an abandoned one, provided the
        // run record says which part failed.
        queued += await SafelyAsync(() => ReviewRemindersAsync(cancellationToken), "review", errors);
        queued += await SafelyAsync(() => SignatureRemindersAsync(cancellationToken), "signatures", errors);
        queued += await SafelyAsync(() => UnacknowledgedCopyRemindersAsync(cancellationToken), "copy acknowledgement", errors);
        queued += await SafelyAsync(() => RetrievalRemindersAsync(cancellationToken), "copy retrieval", errors);
        queued += await SafelyAsync(() => DispositionRemindersAsync(cancellationToken), "disposition", errors);

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

    private async Task<int> ReviewRemindersAsync(CancellationToken cancellationToken)
    {
        var comingDue = await rules.FindEnabledAsync(NotificationKind.ReviewComingDue, cancellationToken);
        var overdue = await rules.FindEnabledAsync(NotificationKind.ReviewOverdue, cancellationToken);

        if (comingDue.Count == 0 && overdue.Count == 0)
        {
            return 0;
        }

        var today = clock.Today;

        // The widest configured lead time decides how far to look. Each document is then
        // matched against the rule that actually applies to its type.
        var horizon = today.AddDays(comingDue.Select(r => r.LeadDays).DefaultIfEmpty(0).Max());

        var due = await documents.ListDueForReviewAsync(horizon, null, null, cancellationToken);
        var candidates = new List<PendingNotification>();

        foreach (var document in due)
        {
            var isOverdue = document.NextReviewDate!.Value < today;
            var applicable = Resolve(isOverdue ? overdue : comingDue, document.DocumentTypeId);

            if (applicable is null)
            {
                continue;
            }

            var daysUntil = document.NextReviewDate!.Value.DayNumber - today.DayNumber;

            // A coming-due rule with a shorter lead time than the widest one shouldn't fire
            // early just because another type warns sooner.
            if (!isOverdue && daysUntil > applicable.LeadDays)
            {
                continue;
            }

            var context = await DocumentTokensAsync(document, cancellationToken);
            context[MessageTemplate.Tokens.DueDate] = document.NextReviewDate!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            context[MessageTemplate.Tokens.DaysUntilDue] = daysUntil.ToString(CultureInfo.InvariantCulture);
            context[MessageTemplate.Tokens.DaysOverdue] = Math.Max(0, -daysUntil).ToString(CultureInfo.InvariantCulture);

            candidates.AddRange(await ExpandAsync(applicable, document, document.Id, context, null, cancellationToken));
        }

        return await QueueAsync(candidates, cancellationToken);
    }

    private async Task<int> SignatureRemindersAsync(CancellationToken cancellationToken)
    {
        var applicableRules = await rules.FindEnabledAsync(NotificationKind.SignaturePending, cancellationToken);
        if (applicableRules.Count == 0)
        {
            return 0;
        }

        var pending = await signatures.ListPendingForAllAsync(cancellationToken);
        var candidates = new List<PendingNotification>();

        foreach (var request in pending)
        {
            var document = await documents.GetAsync(request.DocumentId, cancellationToken);
            if (document is null)
            {
                continue;
            }

            var rule = Resolve(applicableRules, document.DocumentTypeId);
            if (rule is null)
            {
                continue;
            }

            // Age threshold: a step assigned this morning shouldn't be chased this evening.
            if (clock.UtcNow - request.CreatedAt < TimeSpan.FromDays(rule.LeadDays))
            {
                continue;
            }

            var context = await DocumentTokensAsync(document, cancellationToken);
            context[MessageTemplate.Tokens.StepLabel] = request.StepLabel;
            context[MessageTemplate.Tokens.StepOrder] = request.StepOrder.ToString(CultureInfo.InvariantCulture);

            candidates.AddRange(
                await ExpandAsync(rule, document, request.Id, context, request.UserId, cancellationToken));
        }

        return await QueueAsync(candidates, cancellationToken);
    }

    private async Task<int> UnacknowledgedCopyRemindersAsync(CancellationToken cancellationToken)
    {
        var applicableRules = await rules.FindEnabledAsync(NotificationKind.CopyUnacknowledged, cancellationToken);
        if (applicableRules.Count == 0)
        {
            return 0;
        }

        var widest = applicableRules.Select(r => r.LeadDays).DefaultIfEmpty(0).Max();
        var stale = await distributions.ListUnacknowledgedBeforeAsync(
            clock.UtcNow.AddDays(-widest), cancellationToken);

        var candidates = new List<PendingNotification>();

        foreach (var copy in stale)
        {
            var document = await documents.GetAsync(copy.DocumentId, cancellationToken);
            if (document is null)
            {
                continue;
            }

            var rule = Resolve(applicableRules, document.DocumentTypeId);
            if (rule is null || clock.UtcNow - copy.CreatedAt < TimeSpan.FromDays(rule.LeadDays))
            {
                continue;
            }

            var context = await DocumentTokensAsync(document, cancellationToken);
            AddCopyTokens(context, copy);

            candidates.AddRange(
                await ExpandAsync(rule, document, copy.Id, context, null, cancellationToken, copy));
        }

        return await QueueAsync(candidates, cancellationToken);
    }

    private async Task<int> RetrievalRemindersAsync(CancellationToken cancellationToken)
    {
        var applicableRules = await rules.FindEnabledAsync(NotificationKind.CopyRetrievalRequired, cancellationToken);
        if (applicableRules.Count == 0)
        {
            return 0;
        }

        var outstanding = await distributions.ListPendingRetrievalAsync(null, cancellationToken);
        var candidates = new List<PendingNotification>();

        foreach (var (copy, document) in outstanding)
        {
            var rule = Resolve(applicableRules, document.DocumentTypeId);
            if (rule is null)
            {
                continue;
            }

            var context = await DocumentTokensAsync(document, cancellationToken);
            AddCopyTokens(context, copy);

            candidates.AddRange(
                await ExpandAsync(rule, document, copy.Id, context, null, cancellationToken, copy));
        }

        return await QueueAsync(candidates, cancellationToken);
    }

    private async Task<int> DispositionRemindersAsync(CancellationToken cancellationToken)
    {
        var applicableRules = await rules.FindEnabledAsync(NotificationKind.DispositionDue, cancellationToken);
        if (applicableRules.Count == 0)
        {
            return 0;
        }

        var today = clock.Today;
        var due = await documents.ListDueForDispositionAsync(today, null, cancellationToken);
        var candidates = new List<PendingNotification>();

        foreach (var document in due)
        {
            var rule = Resolve(applicableRules, document.DocumentTypeId);
            if (rule is null)
            {
                continue;
            }

            var context = await DocumentTokensAsync(document, cancellationToken);
            context[MessageTemplate.Tokens.RetainUntil] =
                document.RetainUntil?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
            context[MessageTemplate.Tokens.DaysOverdue] = document.RetainUntil is { } until
                ? Math.Max(0, today.DayNumber - until.DayNumber).ToString(CultureInfo.InvariantCulture)
                : "0";

            candidates.AddRange(await ExpandAsync(rule, document, document.Id, context, null, cancellationToken));
        }

        return await QueueAsync(candidates, cancellationToken);
    }

    /// <summary>Most-specific-wins, same precedence as every other policy in the system.</summary>
    private static NotificationRule? Resolve(IReadOnlyList<NotificationRule> candidates, Guid documentTypeId) =>
        candidates
            .Where(r => r.DocumentTypeId is null || r.DocumentTypeId == documentTypeId)
            .OrderByDescending(r => r.Specificity)
            .FirstOrDefault();

    /// <summary>
    /// Turns one rule plus one subject into a notification per resolved recipient, rendering
    /// the templates with that recipient's own tokens.
    /// </summary>
    private async Task<List<PendingNotification>> ExpandAsync(
        NotificationRule rule,
        ControlledDocument document,
        Guid subjectId,
        Dictionary<string, string> context,
        Guid? stepAssigneeId,
        CancellationToken cancellationToken,
        DocumentDistribution? copy = null)
    {
        var recipients = await ResolveRecipientsAsync(rule, document, stepAssigneeId, copy, cancellationToken);
        var period = rule.PeriodKeyFor(clock.Today);
        var results = new List<PendingNotification>();

        foreach (var recipient in recipients)
        {
            var values = new Dictionary<string, string>(context, StringComparer.Ordinal)
            {
                [MessageTemplate.Tokens.Recipient] = recipient.UserName,
                [MessageTemplate.Tokens.RecipientFullName] = recipient.FullName,
            };

            results.Add(new PendingNotification(
                recipient,
                rule.Kind,
                MessageTemplate.Render(rule.SubjectTemplate, values),
                MessageTemplate.Render(rule.BodyTemplate, values),
                $"{rule.Kind}:{subjectId:N}:{recipient.Id:N}:{period}",
                document.Id));
        }

        return results;
    }

    private async Task<IReadOnlyList<DmsUser>> ResolveRecipientsAsync(
        NotificationRule rule,
        ControlledDocument document,
        Guid? stepAssigneeId,
        DocumentDistribution? copy,
        CancellationToken cancellationToken)
    {
        switch (rule.RecipientMode)
        {
            case NotificationRecipientMode.DocumentAuthor:
                return await OneAsync(() => users.GetByUserNameAsync(document.Author, cancellationToken));

            case NotificationRecipientMode.CopyIssuer:
                return copy is null
                    ? []
                    : await OneAsync(() => users.GetByUserNameAsync(copy.IssuedBy, cancellationToken));

            case NotificationRecipientMode.StepAssignee:
                return stepAssigneeId is { } assignee
                    ? await OneAsync(() => users.GetAsync(assignee, cancellationToken))
                    : [];

            case NotificationRecipientMode.RoleHolders:
            {
                if (rule.RecipientRoleId is not { } roleId)
                {
                    return [];
                }

                // Scoped to the document's own site and department, so "the QA head" means the
                // one at the plant that owns this SOP — not every QA head in the company.
                var assignments = await roles.ListAssignmentsAsync(null, roleId, cancellationToken);

                var userIds = assignments
                    .Where(a => a.AppliesTo(document.SiteId, document.DepartmentId))
                    .Select(a => a.UserId)
                    .Distinct()
                    .ToList();

                var resolved = new List<DmsUser>();
                foreach (var userId in userIds)
                {
                    var user = await users.GetAsync(userId, cancellationToken);
                    if (user is { IsActive: true })
                    {
                        resolved.Add(user);
                    }
                }

                return resolved;
            }

            default:
                return [];
        }
    }

    private static async Task<IReadOnlyList<DmsUser>> OneAsync(Func<Task<DmsUser?>> lookup)
    {
        var user = await lookup();
        return user is { IsActive: true } ? [user] : [];
    }

    private async Task<Dictionary<string, string>> DocumentTokensAsync(
        ControlledDocument document,
        CancellationToken cancellationToken)
    {
        var site = await sites.GetAsync(document.SiteId, cancellationToken);
        var department = await departments.GetAsync(document.DepartmentId, cancellationToken);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MessageTemplate.Tokens.DocumentNumber] = document.DocumentNumber,
            [MessageTemplate.Tokens.Title] = document.Title,
            [MessageTemplate.Tokens.Revision] = DocumentNumberFormat.ComposeRevision(document.Revision),
            [MessageTemplate.Tokens.Status] = document.Status.ToString(),
            [MessageTemplate.Tokens.Site] = site?.Name ?? "",
            [MessageTemplate.Tokens.Department] = department?.Name ?? "",
        };
    }

    private static void AddCopyTokens(Dictionary<string, string> context, DocumentDistribution copy)
    {
        context[MessageTemplate.Tokens.CopyNumber] = copy.CopyNumber.ToString(CultureInfo.InvariantCulture);
        context[MessageTemplate.Tokens.CopyType] = copy.CopyType.ToString();
        context[MessageTemplate.Tokens.IssuedTo] = copy.IssuedToName;
        context[MessageTemplate.Tokens.IssuedOn] = copy.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Filters out anything already queued under the same key, then persists the rest. The bulk
    /// key check keeps a sweep to one round trip; the unique index on the column is what
    /// actually guarantees uniqueness when two instances race.
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
        var existing = (await notifications.FindExistingDedupeKeysAsync(keys, cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

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

    private static async Task<int> SafelyAsync(Func<Task<int>> section, string label, List<string> errors)
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
