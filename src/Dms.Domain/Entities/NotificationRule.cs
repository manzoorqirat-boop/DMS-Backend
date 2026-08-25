using Dms.Domain.Common;
using Dms.Domain.Enums;
using Dms.Domain.Services;

namespace Dms.Domain.Entities;

/// <summary>
/// Configures one kind of notification: whether it is sent at all, how far ahead, how often it
/// repeats, who receives it and what it says.
/// <para>
/// This is the last piece of the reminder system that used to be code. Before it, the sweep
/// had a fixed 30-day window, a fixed 7-day chase threshold, message text compiled into the
/// job, and "notify the document author" — none of which survives contact with a second
/// customer. Now an administrator changes any of it without a deployment.
/// </para>
/// <para>
/// What stays in code: which <see cref="NotificationKind"/>s exist, which
/// <see cref="NotificationRecipientMode"/>s can be resolved, and the token set for each kind.
/// Each corresponds to real logic, so offering an option nothing implements would produce a
/// rule that silently notifies nobody.
/// </para>
/// </summary>
public class NotificationRule : Entity, ITimestamped
{
    private NotificationRule() { }

    public NotificationRule(
        NotificationKind kind,
        Guid? documentTypeId,
        NotificationRecipientMode recipientMode,
        Guid? recipientRoleId,
        int leadDays,
        int repeatEveryDays,
        string subjectTemplate,
        string bodyTemplate,
        string createdBy)
    {
        Kind = kind;
        DocumentTypeId = documentTypeId;
        RecipientMode = recipientMode;

        RecipientRoleId = recipientMode == NotificationRecipientMode.RoleHolders
            ? recipientRoleId ?? throw new ArgumentException(
                "A role is required when notifying role holders.", nameof(recipientRoleId))
            : null;

        LeadDays = leadDays >= 0
            ? leadDays
            : throw new ArgumentOutOfRangeException(nameof(leadDays), leadDays, "Lead days cannot be negative.");

        RepeatEveryDays = repeatEveryDays >= 0
            ? repeatEveryDays
            : throw new ArgumentOutOfRangeException(
                nameof(repeatEveryDays), repeatEveryDays, "Repeat interval cannot be negative.");

        SubjectTemplate = RequireTemplate(subjectTemplate, kind, nameof(subjectTemplate));
        BodyTemplate = RequireTemplate(bodyTemplate, kind, nameof(bodyTemplate));

        CreatedBy = string.IsNullOrWhiteSpace(createdBy)
            ? throw new ArgumentException("Notification rules must be attributable.", nameof(createdBy))
            : createdBy;

        CreatedAt = DateTimeOffset.UtcNow;
    }

    public NotificationKind Kind { get; private set; }

    /// <summary>Null means this rule applies to every document type.</summary>
    public Guid? DocumentTypeId { get; private set; }

    public bool IsEnabled { get; private set; } = true;

    public NotificationRecipientMode RecipientMode { get; private set; }

    /// <summary>Set only when <see cref="RecipientMode"/> is RoleHolders.</summary>
    public Guid? RecipientRoleId { get; private set; }

    /// <summary>
    /// How many days before the due date to start notifying. Ignored by kinds that aren't
    /// date-driven, and for those it is the age threshold instead — an unacknowledged copy is
    /// chased once it is <see cref="LeadDays"/> old.
    /// </summary>
    public int LeadDays { get; private set; }

    /// <summary>
    /// 0 sends once per subject; any positive value repeats on that cadence while the
    /// condition persists.
    /// <para>
    /// Separating these matters. A coming-due warning that repeated daily through a 90-day
    /// window would be muted by its recipients within a week, and a muted reminder system is
    /// the same as none. An overdue item, by contrast, should keep arriving.
    /// </para>
    /// </summary>
    public int RepeatEveryDays { get; private set; }

    public string SubjectTemplate { get; private set; } = "";

    public string BodyTemplate { get; private set; } = "";

    public string CreatedBy { get; private set; } = "";

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    /// <summary>Type-specific rules beat the catch-all, same precedence as every other policy here.</summary>
    public int Specificity => DocumentTypeId is null ? 0 : 1;

    public void Update(
        NotificationRecipientMode recipientMode,
        Guid? recipientRoleId,
        int leadDays,
        int repeatEveryDays,
        string subjectTemplate,
        string bodyTemplate)
    {
        RecipientMode = recipientMode;

        RecipientRoleId = recipientMode == NotificationRecipientMode.RoleHolders
            ? recipientRoleId ?? throw new ArgumentException(
                "A role is required when notifying role holders.", nameof(recipientRoleId))
            : null;

        LeadDays = leadDays >= 0
            ? leadDays
            : throw new ArgumentOutOfRangeException(nameof(leadDays), leadDays, "Lead days cannot be negative.");

        RepeatEveryDays = repeatEveryDays >= 0
            ? repeatEveryDays
            : throw new ArgumentOutOfRangeException(
                nameof(repeatEveryDays), repeatEveryDays, "Repeat interval cannot be negative.");

        SubjectTemplate = RequireTemplate(subjectTemplate, Kind, nameof(subjectTemplate));
        BodyTemplate = RequireTemplate(bodyTemplate, Kind, nameof(bodyTemplate));

        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Disabling is preferred to deleting. A deleted rule looks identical to one that was
    /// never configured, and "why did nobody get warned" is easier to answer when the rule is
    /// still there marked off.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        IsEnabled = enabled;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// The period key for deduplication: an empty string when the rule sends once per subject,
    /// otherwise the current repeat window. This is what turns <see cref="RepeatEveryDays"/>
    /// into behaviour — the dedupe index does the rest.
    /// </summary>
    public string PeriodKeyFor(DateOnly today) =>
        RepeatEveryDays <= 0
            ? "once"
            : (today.DayNumber / RepeatEveryDays).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string RequireTemplate(string template, NotificationKind kind, string paramName)
    {
        var validation = MessageTemplate.Validate(template, NotificationTokens.For(kind));

        return validation.IsValid
            ? template.Trim()
            : throw new ArgumentException(
                $"Invalid template: {string.Join(" ", validation.Issues)}", paramName);
    }
}

/// <summary>
/// Which tokens each notification kind can offer.
/// <para>
/// Declared per kind rather than one universal list so a rule can't reference a copy number on
/// a review reminder — that would validate at save time and render a blank field months later,
/// which is exactly the class of error configuration is supposed to eliminate.
/// </para>
/// </summary>
public static class NotificationTokens
{
    private static readonly string[] Common =
    [
        MessageTemplate.Tokens.DocumentNumber,
        MessageTemplate.Tokens.Title,
        MessageTemplate.Tokens.Revision,
        MessageTemplate.Tokens.Status,
        MessageTemplate.Tokens.Department,
        MessageTemplate.Tokens.Site,
        MessageTemplate.Tokens.Recipient,
        MessageTemplate.Tokens.RecipientFullName,
    ];

    public static IReadOnlyCollection<string> For(NotificationKind kind) => kind switch
    {
        NotificationKind.ReviewComingDue or NotificationKind.ReviewOverdue =>
        [
            .. Common,
            MessageTemplate.Tokens.DueDate,
            MessageTemplate.Tokens.DaysUntilDue,
            MessageTemplate.Tokens.DaysOverdue,
        ],

        NotificationKind.SignaturePending =>
        [
            .. Common,
            MessageTemplate.Tokens.StepLabel,
            MessageTemplate.Tokens.StepOrder,
        ],

        NotificationKind.CopyUnacknowledged or NotificationKind.CopyRetrievalRequired =>
        [
            .. Common,
            MessageTemplate.Tokens.CopyNumber,
            MessageTemplate.Tokens.CopyType,
            MessageTemplate.Tokens.IssuedTo,
            MessageTemplate.Tokens.IssuedOn,
        ],

        NotificationKind.DispositionDue =>
        [
            .. Common,
            MessageTemplate.Tokens.RetainUntil,
            MessageTemplate.Tokens.DaysOverdue,
        ],

        _ => Common,
    };
}
