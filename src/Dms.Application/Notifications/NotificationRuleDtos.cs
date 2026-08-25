using Dms.Domain.Entities;
using Dms.Domain.Enums;

namespace Dms.Application.Notifications;

public sealed record CreateNotificationRuleRequest(
    NotificationKind Kind,
    Guid? DocumentTypeId,
    NotificationRecipientMode RecipientMode,
    Guid? RecipientRoleId,
    int LeadDays,
    int RepeatEveryDays,
    string SubjectTemplate,
    string BodyTemplate);

public sealed record UpdateNotificationRuleRequest(
    NotificationRecipientMode RecipientMode,
    Guid? RecipientRoleId,
    int LeadDays,
    int RepeatEveryDays,
    string SubjectTemplate,
    string BodyTemplate);

public sealed record NotificationRuleView(
    Guid Id,
    NotificationKind Kind,
    Guid? DocumentTypeId,
    string DocumentTypeScope,
    bool IsEnabled,
    NotificationRecipientMode RecipientMode,
    Guid? RecipientRoleId,
    string RecipientRoleCode,
    int LeadDays,
    int RepeatEveryDays,
    string SubjectTemplate,
    string BodyTemplate,
    IReadOnlyList<string> AvailableTokens,
    string CreatedBy,
    DateTimeOffset CreatedAt)
{
    public static NotificationRuleView From(NotificationRule rule, string documentTypeScope, string roleCode) =>
        new(
            rule.Id,
            rule.Kind,
            rule.DocumentTypeId,
            documentTypeScope,
            rule.IsEnabled,
            rule.RecipientMode,
            rule.RecipientRoleId,
            roleCode,
            rule.LeadDays,
            rule.RepeatEveryDays,
            rule.SubjectTemplate,
            rule.BodyTemplate,
            NotificationTokens.For(rule.Kind).ToList(),
            rule.CreatedBy,
            rule.CreatedAt);
}

public sealed record MessagePreview(string Subject, string Body, IReadOnlyList<string> AvailableTokens);

public sealed record PreviewTemplateRequest(
    NotificationKind Kind,
    string SubjectTemplate,
    string BodyTemplate);
