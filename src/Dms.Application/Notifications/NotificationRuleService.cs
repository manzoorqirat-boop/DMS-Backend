using Dms.Application.Abstractions;
using Dms.Application.Common;
using Dms.Domain.Entities;
using Dms.Domain.Enums;
using Dms.Domain.Services;

namespace Dms.Application.Notifications;

/// <summary>
/// Administers notification rules — the configuration that decides which reminders exist, when
/// they fire, who receives them and what they say.
/// </summary>
public sealed class NotificationRuleService(
    INotificationRuleRepository rules,
    IRoleRepository roles,
    IDocumentTypeRepository documentTypes,
    IAccessControl access,
    IAuditTrail audit,
    ICurrentUser currentUser)
{
    private const string EntityType = "NotificationRule";

    /// <summary>Tokens a rule for this kind may use, for an admin UI to offer.</summary>
    public static IReadOnlyCollection<string> TokensFor(NotificationKind kind) => NotificationTokens.For(kind);

    public async Task<Result<NotificationRuleView>> CreateAsync(
        CreateNotificationRuleRequest request,
        CancellationToken cancellationToken)
    {
        var gate = await RequireConfigureAsync(cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        if (request.RecipientMode == NotificationRecipientMode.RoleHolders)
        {
            if (request.RecipientRoleId is not { } roleId
                || await roles.GetAsync(roleId, cancellationToken) is null)
            {
                return Error.Validation(
                    "recipient_role_unknown",
                    "Notifying role holders requires an existing role.");
            }
        }

        NotificationRule rule;
        try
        {
            rule = new NotificationRule(
                request.Kind,
                request.DocumentTypeId,
                request.RecipientMode,
                request.RecipientRoleId,
                request.LeadDays,
                request.RepeatEveryDays,
                request.SubjectTemplate,
                request.BodyTemplate,
                currentUser.UserName!);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return Error.Validation("notification_rule_invalid", ex.Message);
        }

        rules.Add(rule);
        audit.Record(
            AuditAction.NotificationRuleCreated, EntityType, rule.Id, rule.Kind.ToString(),
            Describe(rule));

        var outcome = await rules.SaveChangesAsync(cancellationToken);
        if (!outcome.Saved)
        {
            return outcome.ViolatedIndexContains("scope")
                ? Error.Conflict(
                    "notification_rule_exists",
                    "A rule already exists for that notification kind and document type. Edit it instead.")
                : Error.Conflict("notification_rule_save_conflict", "The rule could not be saved.");
        }

        return await ViewAsync(rule, cancellationToken);
    }

    public async Task<Result<NotificationRuleView>> UpdateAsync(
        Guid ruleId,
        UpdateNotificationRuleRequest request,
        CancellationToken cancellationToken)
    {
        var gate = await RequireConfigureAsync(cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var rule = await rules.GetAsync(ruleId, cancellationToken);
        if (rule is null)
        {
            return Error.NotFound("notification_rule_not_found", $"No notification rule with id {ruleId}.");
        }

        try
        {
            rule.Update(
                request.RecipientMode,
                request.RecipientRoleId,
                request.LeadDays,
                request.RepeatEveryDays,
                request.SubjectTemplate,
                request.BodyTemplate);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return Error.Validation("notification_rule_invalid", ex.Message);
        }

        audit.Record(
            AuditAction.NotificationRuleChanged, EntityType, rule.Id, rule.Kind.ToString(), Describe(rule));

        var outcome = await rules.SaveChangesAsync(cancellationToken);
        return outcome.Saved
            ? await ViewAsync(rule, cancellationToken)
            : Error.Conflict("notification_rule_save_conflict", "The rule could not be updated.");
    }

    /// <summary>
    /// Enables or disables a rule. Disabling is preferred to deleting: a deleted rule looks
    /// identical to one never configured, and "why did nobody get warned" is easier to answer
    /// when the rule is still there, marked off.
    /// </summary>
    public async Task<Result<NotificationRuleView>> SetEnabledAsync(
        Guid ruleId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var gate = await RequireConfigureAsync(cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var rule = await rules.GetAsync(ruleId, cancellationToken);
        if (rule is null)
        {
            return Error.NotFound("notification_rule_not_found", $"No notification rule with id {ruleId}.");
        }

        rule.SetEnabled(enabled);

        audit.Record(
            AuditAction.NotificationRuleChanged, EntityType, rule.Id, rule.Kind.ToString(),
            enabled ? "Enabled." : "Disabled — this reminder will no longer be sent.");

        var outcome = await rules.SaveChangesAsync(cancellationToken);
        return outcome.Saved
            ? await ViewAsync(rule, cancellationToken)
            : Error.Conflict("notification_rule_save_conflict", "The rule could not be updated.");
    }

    public async Task<IReadOnlyList<NotificationRuleView>> ListAsync(
        NotificationKind? kind,
        CancellationToken cancellationToken)
    {
        var found = await rules.ListAsync(kind, cancellationToken);
        var types = await documentTypes.ListAsync(includeInactive: true, cancellationToken);
        var roleList = await roles.ListAsync(includeInactive: true, cancellationToken);

        var typeCodes = types.ToDictionary(t => t.Id, t => t.Code);
        var roleCodes = roleList.ToDictionary(r => r.Id, r => r.Code);

        return found
            .Select(r => NotificationRuleView.From(
                r,
                r.DocumentTypeId is { } id ? typeCodes.GetValueOrDefault(id, "") : "All types",
                r.RecipientRoleId is { } roleId ? roleCodes.GetValueOrDefault(roleId, "") : ""))
            .ToList();
    }

    /// <summary>
    /// Renders a template against sample values so an administrator sees the actual message
    /// before saving. Validation errors surface here rather than in a 2am sweep.
    /// </summary>
    public static Result<MessagePreview> Preview(
        NotificationKind kind,
        string subjectTemplate,
        string bodyTemplate)
    {
        var available = NotificationTokens.For(kind);

        foreach (var template in new[] { subjectTemplate, bodyTemplate })
        {
            var validation = MessageTemplate.Validate(template, available);
            if (!validation.IsValid)
            {
                return Error.Validation("template_invalid", string.Join(" ", validation.Issues));
            }
        }

        var sample = SampleValues();

        return Result<MessagePreview>.Success(new MessagePreview(
            MessageTemplate.Render(subjectTemplate, sample),
            MessageTemplate.Render(bodyTemplate, sample),
            available.ToList()));
    }

    private static Dictionary<string, string> SampleValues() => new(StringComparer.Ordinal)
    {
        [MessageTemplate.Tokens.DocumentNumber] = "MNK-QA-SOP-0001",
        [MessageTemplate.Tokens.Title] = "Cleaning of Vessel V-101",
        [MessageTemplate.Tokens.Revision] = "02",
        [MessageTemplate.Tokens.Status] = "Effective",
        [MessageTemplate.Tokens.Site] = "Mankind Paonta Sahib",
        [MessageTemplate.Tokens.Department] = "Quality Assurance",
        [MessageTemplate.Tokens.DueDate] = "2026-11-30",
        [MessageTemplate.Tokens.DaysUntilDue] = "30",
        [MessageTemplate.Tokens.DaysOverdue] = "0",
        [MessageTemplate.Tokens.Recipient] = "a.nair",
        [MessageTemplate.Tokens.RecipientFullName] = "A Nair",
        [MessageTemplate.Tokens.StepLabel] = "Approved By",
        [MessageTemplate.Tokens.StepOrder] = "3",
        [MessageTemplate.Tokens.CopyNumber] = "4",
        [MessageTemplate.Tokens.CopyType] = "Controlled",
        [MessageTemplate.Tokens.IssuedTo] = "Production Floor 2",
        [MessageTemplate.Tokens.IssuedOn] = "2026-06-01",
        [MessageTemplate.Tokens.RetainUntil] = "2031-06-01",
    };

    private async Task<NotificationRuleView> ViewAsync(NotificationRule rule, CancellationToken cancellationToken)
    {
        var typeCode = rule.DocumentTypeId is { } typeId
            ? (await documentTypes.GetAsync(typeId, cancellationToken))?.Code ?? ""
            : "All types";

        var roleCode = rule.RecipientRoleId is { } roleId
            ? (await roles.GetAsync(roleId, cancellationToken))?.Code ?? ""
            : "";

        return NotificationRuleView.From(rule, typeCode, roleCode);
    }

    private static string Describe(NotificationRule rule) =>
        $"{rule.Kind}: recipients {rule.RecipientMode}, lead {rule.LeadDays}d, "
        + (rule.RepeatEveryDays <= 0 ? "sent once." : $"repeats every {rule.RepeatEveryDays}d.");

    private async Task<Error?> RequireConfigureAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentUser.UserName))
        {
            return Error.Validation("actor_unknown", "The acting user could not be determined.");
        }

        var allowed = await access.HasPermissionAsync(
            Permission.WorkflowConfigure, siteId: null, departmentId: null, cancellationToken);

        return allowed
            ? null
            : Error.Validation(
                "permission_denied",
                $"{Permission.WorkflowConfigure} at organisation-wide scope is required to configure notifications.");
    }
}
