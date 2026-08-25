namespace Dms.Domain.Enums;

/// <summary>
/// Lifecycle of a registered <see cref="Dms.Domain.Entities.DocumentTemplate"/>.
/// <para>
/// Linear and one-directional except for the terminal <see cref="Retired"/> state: a
/// template is uploaded, validated once, and either becomes the thing new documents are
/// cloned from or gets fixed and re-uploaded as a new version. There's no "edit in place" —
/// see <see cref="Dms.Domain.Entities.DocumentTemplate"/> for why versions are immutable once created.
/// </para>
/// </summary>
public enum TemplateStatus
{
    /// <summary>Uploaded, not yet run through <see cref="Dms.Domain.Services.DocxTemplateValidator"/>.</summary>
    PendingValidation,

    /// <summary>Passed structural validation; eligible to be made <see cref="Active"/>.</summary>
    ValidationPassed,

    /// <summary>Failed structural validation. See <see cref="Dms.Domain.Entities.DocumentTemplate.ValidationIssues"/> for why.</summary>
    ValidationFailed,

    /// <summary>The live template new documents of this type are cloned from. At most one per DocumentType.</summary>
    Active,

    /// <summary>Superseded by a newer Active version. Documents already created from it keep referencing it by version.</summary>
    Retired,
}

/// <summary>
/// Lifecycle of a <see cref="Dms.Domain.Entities.ControlledDocument"/>, as described by the URS:
/// initiation, review, approval, issuance, distribution, retrieval, revision, obsolescence.
/// <para>
/// The full set is declared now because the states are URS-driven and known, but only the
/// Draft-stage transitions are implemented — review and approval are ERES's to drive
/// (Phase 5), and writing speculative transition methods for them would mean rewriting them
/// once the real envelope callbacks exist.
/// </para>
/// </summary>
public enum DocumentStatus
{
    /// <summary>Created from a template, being authored. The only editable state.</summary>
    Draft,

    /// <summary>Handed to ERES as an envelope; awaiting reviewer sign-off.</summary>
    InReview,

    /// <summary>All approvers signed. Not yet in force.</summary>
    Approved,

    /// <summary>In force from its effective date. The version a controlled copy prints from.</summary>
    Effective,

    /// <summary>Replaced by a later revision that is now Effective.</summary>
    Superseded,

    /// <summary>Withdrawn from use with no replacement. Retained for the retention period.</summary>
    Obsolete,

    /// <summary>Abandoned before ever being issued. The number stays burned rather than reused.</summary>
    Withdrawn,
}

/// <summary>
/// What happened, for an <see cref="Dms.Domain.Entities.AuditEvent"/>.
/// <para>
/// An enum rather than a free string so the set is closed and greppable: an auditor asking
/// "show me every activation" must not depend on whoever wrote the call site having spelled
/// it the same way as everyone else.
/// </para>
/// </summary>
public enum AuditAction
{
    // Master data
    DocumentTypeCreated,
    DocumentTypeDeactivated,
    DocumentTypeReactivated,
    SiteCreated,
    DepartmentCreated,

    // Templates
    TemplateRegistered,
    TemplateValidationPassed,
    TemplateValidationFailed,
    TemplateActivated,
    TemplateRetired,

    // Controlled documents
    DocumentCreated,
    DocumentRetitled,
    DocumentWithdrawn,

    /// <summary>
    /// A saved working copy failed protected-field revalidation — its metadata or its
    /// document protection had been altered. Recorded as an event in its own right because
    /// an attempt to defeat the lock is exactly the kind of thing the trail exists to show.
    /// </summary>
    DocumentIntegrityCheckFailed,

    DocumentIntegrityCheckPassed,

    // Review and approval — driven by ERES envelope outcomes, not by DMS deciding.
    DocumentSubmittedForReview,
    DocumentReturnedForRework,
    DocumentApproved,
    DocumentMadeEffective,
    DocumentSuperseded,
    DocumentObsoleted,
    ReviewRouteStarted,
    ReviewRouteCancelled,
    SignatureApplied,
    SignatureRejected,

    /// <summary>
    /// A signing attempt failed password re-authentication. Recorded because repeated failures
    /// against a signature step are exactly what §11.300(d) expects to be detected and
    /// reported, and an unrecorded failed attempt tells nobody anything.
    /// </summary>
    SignatureAuthenticationFailed,

    UserCreated,
    UserDeactivated,
    UserPasswordChanged,

    // Access control. Privilege changes are themselves auditable events — "who could do what,
    // and when did that change" is one of the first questions in any access review.
    RoleCreated,
    RolePermissionsChanged,
    RoleDeactivated,
    RoleAssigned,
    RoleRevoked,

    // Configuration. A numbering pattern change alters every number issued afterwards, so it
    // belongs in the trail alongside the documents it will shape.
    NumberingRuleCreated,
    NumberingRuleChanged,
    WorkflowDefinitionCreated,
    WorkflowStepsChanged,
    WorkflowActivated,
    WorkflowDeactivated,
    MetadataFieldAdded,
    MetadataFieldChanged,
    MetadataFieldRemoved,
    DocumentRevisionStarted,
    DocumentPeriodicReviewRecorded,
    ReviewPolicyCreated,
    ReviewPolicyChanged,

    // Distribution and controlled printing
    CopyIssued,
    CopyAcknowledged,
    CopyRetrieved,
    CopyClosedOut,
    CopyPrinted,
    CopyPrintRefused,

    // Scheduled jobs and notifications
    ScheduledJobRan,
    NotificationQueued,
    NotificationRuleCreated,
    NotificationRuleChanged,

    // Retention and disposition
    RetentionPolicyCreated,
    RetentionPolicyChanged,
    RetentionClockStarted,
    DocumentContentDestroyed,
    DocumentRetainedPermanently,

    // Editing sessions (check-out / check-in)
    DocumentCheckedOut,
    DocumentCheckedIn,
    EditingSaveAccepted,
    EditingSaveRejected,
    EditingSessionForceClosed,

    // Authentication. Distinct from the signing events — these concern the general login
    // credential, which §11.200 keeps separate from the e-signature credential.
    UserLoggedIn,
    UserLoginFailed,
    UserLoginLockedOut,
}

/// <summary>Why a person is on a signature route.</summary>
public enum SignatureRole
{
    Reviewer,
    Approver,
}

/// <summary>
/// State of one step on a route. Steps run in order: only the lowest-numbered Pending step is
/// signable at any moment, which is what makes the route sequential rather than a free-for-all.
/// </summary>
public enum SignatureRequestStatus
{
    Pending,
    Signed,
    Rejected,

    /// <summary>The route was abandoned — rejected earlier, or the document withdrawn — before this step was reached.</summary>
    Cancelled,
}

/// <summary>
/// The meaning of a signature, as 21 CFR Part 11 §11.50(a)(3) requires be displayed with it.
/// A signature that doesn't say what it meant is not a compliant signature.
/// </summary>
public enum SignatureMeaning
{
    Reviewed,
    Approved,
    Rejected,
}

/// <summary>
/// A single privilege that can be granted to a role. The rows of the privilege matrix.
/// <para>
/// An enum rather than free-form strings held in master data, deliberately. Roles and their
/// grants are configuration — an administrator composes them without a deployment. The set of
/// <i>things that can be granted</i> is not: every value here corresponds to a specific check
/// in code, so a permission that exists but nothing enforces would be a privilege matrix that
/// lies to the person reading it. Adding a permission means adding its enforcement.
/// </para>
/// </summary>
public enum Permission
{
    // Master data
    SiteManage,
    DepartmentManage,
    DocumentTypeManage,

    // Access control — separated from other master data because the power to grant
    // yourself a permission is categorically different from the power to add a department.
    RoleManage,
    UserManage,

    // Configuration
    NumberingConfigure,
    WorkflowConfigure,

    // Templates
    TemplateView,
    TemplateRegister,
    TemplateActivate,
    TemplateRetire,

    // Controlled documents
    DocumentView,
    DocumentCreate,
    DocumentEdit,
    DocumentWithdraw,
    DocumentSubmit,

    /// <summary>
    /// Permission to appear on a signature route at all. Not permission to sign a specific
    /// step — that is always and only the person named on it, checked separately.
    /// </summary>
    DocumentSign,

    DocumentIssue,
    DocumentObsolete,

    /// <summary>Read the audit trail. There is deliberately no permission to write or amend it.</summary>
    AuditView,
}

/// <summary>
/// How far a role assignment reaches. Narrower is more specific and is what makes "QA Head at
/// Site A" different from "QA Head at Site B" — a distinction a global-only model can't express
/// and which matters the moment a company has two plants.
/// </summary>
public enum AssignmentScope
{
    /// <summary>Everywhere. Reserved for genuinely organisation-wide roles.</summary>
    Global,

    /// <summary>All departments at one site.</summary>
    Site,

    /// <summary>One department at one site.</summary>
    Department,
}

/// <summary>
/// Which piece of DMS data fills a metadata content control.
/// <para>
/// A closed enum rather than, say, a property-path string, for the same reason
/// <see cref="Permission"/> is: every value corresponds to real code that produces the value.
/// A configurable expression language here would be a small unvalidated programming language
/// sitting inside a regulated system — the thing to avoid, not the thing to build.
/// </para>
/// </summary>
public enum MetadataSource
{
    /// <summary>The issued document number, rendered from the numbering pattern.</summary>
    DocumentNumber,

    DocumentTitle,

    /// <summary>Revision label, e.g. "00".</summary>
    Revision,

    /// <summary>Blank until the document is issued. Deliberately not guessed at on a draft.</summary>
    EffectiveDate,

    DepartmentName,
    DepartmentCode,
    SiteName,
    SiteCode,
    DocumentTypeName,
    DocumentTypeCode,

    /// <summary>Username of the author who created the document.</summary>
    Author,

    /// <summary>Author's full name as held on their user record at creation time.</summary>
    AuthorFullName,

    /// <summary>Date the draft was created, ISO format.</summary>
    CreatedDate,

    /// <summary>Current document status, e.g. "Draft".</summary>
    Status,
}

/// <summary>
/// What kind of copy was issued, which decides how it is watermarked and whether it is owed
/// back at the end of the document's life.
/// </summary>
public enum CopyType
{
    /// <summary>
    /// Tracked, numbered, and must be retrieved when the document is superseded or withdrawn.
    /// The only kind anyone is permitted to work from.
    /// </summary>
    Controlled,

    /// <summary>
    /// Issued for information only and never retrieved. Watermarked to say so, because an
    /// uncontrolled copy that doesn't announce itself is indistinguishable from a controlled
    /// one the moment it leaves the printer.
    /// </summary>
    Uncontrolled,

    /// <summary>
    /// Issued to an external party — auditor, regulator, contract partner. Tracked like a
    /// controlled copy but recorded separately, since "who outside the company holds our
    /// procedures" is its own question.
    /// </summary>
    External,
}

public enum DistributionStatus
{
    /// <summary>Sent, not yet confirmed as received.</summary>
    Issued,

    /// <summary>Recipient confirmed receipt.</summary>
    Acknowledged,

    /// <summary>Physically collected back. The normal end for a controlled copy.</summary>
    Retrieved,

    /// <summary>Destroyed on site instead of returned, with a recorded note.</summary>
    Destroyed,

    /// <summary>Could not be accounted for. Deliberately a distinct outcome, not folded into
    /// Destroyed — an unaccounted controlled copy is a finding and must read as one.</summary>
    Lost,
}

/// <summary>Why a notification was raised. Also the dedupe dimension for scheduled reminders.</summary>
public enum NotificationKind
{
    /// <summary>A document is approaching its periodic-review due date.</summary>
    ReviewComingDue,

    /// <summary>A document's review date has passed.</summary>
    ReviewOverdue,

    /// <summary>A signature step is waiting on this user.</summary>
    SignaturePending,

    /// <summary>A controlled copy has been issued but not acknowledged.</summary>
    CopyUnacknowledged,

    /// <summary>Copies are still in circulation for a document no longer current.</summary>
    CopyRetrievalRequired,

    /// <summary>A record's retention has expired and awaits a disposition decision.</summary>
    DispositionDue,
}

/// <summary>
/// Who a notification rule sends to. A closed set, because each value corresponds to real
/// resolution code — an option nothing resolves would produce a rule that silently notifies
/// nobody, which is the worst possible failure for a reminder system.
/// </summary>
public enum NotificationRecipientMode
{
    /// <summary>The document's author. Closest attributable person, often not the right one.</summary>
    DocumentAuthor,

    /// <summary>
    /// Everyone holding a named role at the document's own site and department. The usual
    /// choice — "the QA head at the plant that owns this SOP", without needing a separate
    /// department-owner field to maintain.
    /// </summary>
    RoleHolders,

    /// <summary>Whoever issued the controlled copy. Only meaningful for copy-related kinds.</summary>
    CopyIssuer,

    /// <summary>The person the pending signature step is assigned to.</summary>
    StepAssignee,
}

public enum NotificationStatus
{
    /// <summary>Queued, not yet handed to a sender.</summary>
    Pending,

    Sent,

    /// <summary>
    /// Delivery failed. Kept rather than retried indefinitely — a notification nobody received
    /// should be visible as such, not silently absent.
    /// </summary>
    Failed,
}

public enum JobRunStatus
{
    Succeeded,

    /// <summary>Ran to completion but something within it failed. The detail says what.</summary>
    CompletedWithErrors,

    Failed,
}

/// <summary>When a document's retention clock starts.</summary>
public enum RetentionTrigger
{
    /// <summary>From the date the version was replaced by a successor.</summary>
    Superseded,

    /// <summary>From the date the document was withdrawn from use entirely.</summary>
    Obsolete,
}

/// <summary>
/// What happens to a record once its retention period expires. Always a decision someone
/// records, never something the system performs on a timer — see <c>RetentionService</c>.
/// </summary>
public enum DispositionAction
{
    /// <summary>
    /// The stored file is deleted. The register row, its signatures and its audit trail are
    /// kept — destroying the metadata would destroy the evidence that the document ever
    /// existed and was properly controlled, which is not what a retention period permits.
    /// </summary>
    DestroyContent,

    /// <summary>
    /// Kept indefinitely. Some records — validation master plans, product dossiers — outlive
    /// any schedule and are marked so they stop appearing on the disposition worklist.
    /// </summary>
    RetainPermanently,
}

public enum EditingSessionStatus
{
    /// <summary>Open. The document is checked out and nobody else may edit it.</summary>
    Active,

    /// <summary>Closed normally — the author finished and the file was saved back.</summary>
    CheckedIn,

    /// <summary>Closed with no changes saved.</summary>
    Abandoned,

    /// <summary>
    /// Force-closed by an administrator. Distinct from Abandoned because breaking someone
    /// else's lock is an intervention that needs to be visible as one.
    /// </summary>
    ForceClosed,
}
