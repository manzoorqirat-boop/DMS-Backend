using Dms.Domain.Entities;
using Dms.Domain.Enums;
using Dms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dms.Api;

/// <summary>
/// Creates the first administrator when the database has no users at all.
/// <para>
/// Necessary because authorisation denies by default: without a seeded account there is nobody
/// who can log in to create one, and no endpoint that would let them. The alternative — leaving
/// user creation open to anonymous callers — would mean a window on every fresh deployment
/// where anyone reaching the API could make themselves an administrator.
/// </para>
/// <para>
/// Runs <b>only</b> when the user table is empty. It is not an upsert and never modifies an
/// existing account, so leaving the configuration in place cannot reset a real administrator's
/// password later.
/// </para>
/// </summary>
public static class BootstrapSeeder
{
    public const string SectionName = "Bootstrap";

    public const string UserNameKey = $"{SectionName}:AdminUserName";
    public const string PasswordKey = $"{SectionName}:AdminPassword";
    public const string FullNameKey = $"{SectionName}:AdminFullName";

    /// <summary>Code of the role granted every permission, created alongside the first admin.</summary>
    public const string AdminRoleCode = "SYSTEM_ADMIN";

    public static async Task SeedAsync(IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();

        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var db = scope.ServiceProvider.GetRequiredService<DmsDbContext>();

        if (await db.Users.AnyAsync(cancellationToken))
        {
            return;
        }

        var userName = configuration[UserNameKey];
        var password = configuration[PasswordKey];

        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
        {
            // Warned loudly rather than seeding a default account with a known password. A
            // predictable bootstrap credential on a regulated system is worse than a system
            // nobody can log into yet.
            logger.LogWarning(
                "No users exist and no bootstrap administrator is configured. Set {UserKey} and "
                + "{PasswordKey} to create one; until then, nobody can sign in.",
                UserNameKey, PasswordKey);
            return;
        }

        DmsUser admin;
        Role adminRole;

        try
        {
            admin = new DmsUser(
                userName.Trim(),
                configuration[FullNameKey] ?? userName.Trim(),
                department: "System",
                designation: "System Administrator",
                password);

            adminRole = new Role(
                AdminRoleCode,
                "System Administrator",
                "Created at first run. Holds every permission.",
                isSystem: true);

            adminRole.SetPermissions(Enum.GetValues<Permission>());
        }
        catch (ArgumentException ex)
        {
            logger.LogError(ex, "Bootstrap administrator could not be created: {Message}", ex.Message);
            return;
        }

        db.Users.Add(admin);
        db.Roles.Add(adminRole);

        // Global scope, because RoleManage is deliberately not scopable — a site-scoped
        // administrator could never grant anyone else anything.
        db.UserRoleAssignments.Add(new UserRoleAssignment(
            admin.Id, adminRole.Id, siteId: null, departmentId: null, assignedBy: admin.UserName));

        db.AuditEvents.Add(new AuditEvent(
            AuditAction.UserCreated, "DmsUser", admin.Id, admin.UserName, admin.UserName,
            "Bootstrap administrator created on first run because the system had no users."));

        db.AuditEvents.Add(new AuditEvent(
            AuditAction.RoleAssigned, "UserRoleAssignment", adminRole.Id,
            $"{admin.UserName} → {AdminRoleCode}", admin.UserName,
            "Granted every permission at organisation-wide scope by the first-run bootstrap."));

        SeedDefaultNotificationRules(db, admin.UserName);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Bootstrap administrator '{UserName}' created. Change this password immediately and "
            + "remove {PasswordKey} from configuration.",
            admin.UserName, PasswordKey);
    }

    /// <summary>
    /// Seeds a starting set of notification rules.
    /// <para>
    /// Seeded as <b>data</b> rather than left as code defaults, deliberately. The sweep fires
    /// nothing for a kind with no rule, so without this a fresh system is silent — but the
    /// alternative of hardcoding fallbacks would put the wording, timing and recipients back in
    /// the codebase where no administrator can reach them. These rows are ordinary
    /// configuration from the moment they exist: editable, disableable, and visible in the UI.
    /// </para>
    /// <para>
    /// Recipients default to the document author because a fresh system has no roles beyond
    /// the bootstrap administrator, so there is nobody else to point at yet. Repointing these
    /// at the right role holders is the first thing to configure after creating them.
    /// </para>
    /// </summary>
    private static void SeedDefaultNotificationRules(DmsDbContext db, string createdBy)
    {
        var defaults = new (NotificationKind Kind, int Lead, int Repeat, string Subject, string Body)[]
        {
            (NotificationKind.ReviewComingDue, 30, 0,
                "Review due in {DaysUntilDue} day(s): {DocumentNumber}",
                "{DocumentNumber} Rev {Revision} — \"{Title}\" ({Department}) is due for periodic "
                + "review on {DueDate}. Record a review, or start a revision if changes are needed."),

            // Repeats daily: an overdue controlled document should keep arriving until someone
            // acts, unlike the coming-due warning which would become noise.
            (NotificationKind.ReviewOverdue, 0, 1,
                "OVERDUE for review: {DocumentNumber}",
                "{DocumentNumber} Rev {Revision} — \"{Title}\" was due for review on {DueDate}, "
                + "{DaysOverdue} day(s) ago. It remains effective and in use until reviewed or revised."),

            (NotificationKind.SignaturePending, 2, 3,
                "Signature required: {DocumentNumber}",
                "{DocumentNumber} Rev {Revision} — \"{Title}\" is waiting on your signature at "
                + "step {StepOrder} ({StepLabel})."),

            (NotificationKind.CopyUnacknowledged, 7, 7,
                "Copy {CopyNumber} not acknowledged: {DocumentNumber}",
                "{CopyType} copy {CopyNumber} of {DocumentNumber} was issued to {IssuedTo} on "
                + "{IssuedOn} and has not been acknowledged."),

            (NotificationKind.CopyRetrievalRequired, 0, 7,
                "Retrieve copy {CopyNumber}: {DocumentNumber}",
                "{DocumentNumber} Rev {Revision} is {Status}, but {CopyType} copy {CopyNumber} is "
                + "still held by {IssuedTo}. Collect it, or record it as destroyed or lost."),

            (NotificationKind.DispositionDue, 0, 30,
                "Retention expired: {DocumentNumber}",
                "{DocumentNumber} Rev {Revision} was retained until {RetainUntil}, {DaysOverdue} "
                + "day(s) ago, and awaits a disposition decision. Nothing is destroyed automatically."),
        };

        foreach (var (kind, lead, repeat, subject, body) in defaults)
        {
            db.NotificationRules.Add(new NotificationRule(
                kind,
                documentTypeId: null,
                NotificationRecipientMode.DocumentAuthor,
                recipientRoleId: null,
                lead,
                repeat,
                subject,
                body,
                createdBy));
        }
    }
}
