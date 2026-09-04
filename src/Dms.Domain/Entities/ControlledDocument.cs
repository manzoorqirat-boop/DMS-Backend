using Dms.Domain.Common;
using Dms.Domain.Enums;

namespace Dms.Domain.Entities;

/// <summary>
/// A controlled document — the thing the whole system exists to govern. Created as a Draft
/// from the Active template for its type, then moved through review, approval, issuance and
/// eventually obsolescence.
/// <para>
/// <see cref="TemplateId"/> pins the exact template version this document was created from
/// and is never updated. That's what makes activating a new template version safe: it changes
/// what <i>future</i> documents are cloned from and rewrites nothing already in flight.
/// </para>
/// <para>
/// Only the Draft-stage transitions are implemented here. Review and approval are delegated
/// to ERES/Hastakshar (Phase 5), so the states between <see cref="DocumentStatus.InReview"/>
/// and <see cref="DocumentStatus.Approved"/> will be driven by envelope outcomes rather than
/// by methods on this entity, and are deliberately left unwritten rather than guessed at.
/// </para>
/// </summary>
public class ControlledDocument : Entity, ITimestamped
{
    private ControlledDocument() { }

    public ControlledDocument(
        string documentNumber,
        string title,
        Guid siteId,
        Guid departmentId,
        Guid documentTypeId,
        Guid templateId,
        string workingCopyKey,
        string author)
    {
        DocumentNumber = RequireNonEmpty(documentNumber, nameof(documentNumber));
        Title = RequireNonEmpty(title, nameof(title)).Trim();
        SiteId = siteId;
        DepartmentId = departmentId;
        DocumentTypeId = documentTypeId;
        TemplateId = templateId;
        WorkingCopyKey = RequireNonEmpty(workingCopyKey, nameof(workingCopyKey));
        Author = RequireNonEmpty(author, nameof(author));
        Revision = 0;
        Status = DocumentStatus.Draft;

        // A new document founds its own lineage. Revisions inherit this id, which is what
        // makes "every version of this SOP" a single indexed query rather than a string match
        // on document numbers.
        FamilyId = Id;
        IsCurrentRevision = true;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Creates an annexure bound to <paramref name="parent"/>.
    /// <para>
    /// The number is derived from the parent's — <c>ND-QIC-SOP-0042-A1</c> — so the
    /// relationship is legible on a printed page with no access to the system. Someone holding
    /// a loose form can tell which procedure it belongs to.
    /// </para>
    /// <para>
    /// It starts in the parent's current status rather than always as a Draft: an annexure
    /// added to a document already in review must not appear to be an editable draft, and one
    /// added to an effective document is in force immediately because its parent is.
    /// </para>
    /// </summary>
    public static ControlledDocument CreateAnnexure(
        ControlledDocument parent,
        int annexureNumber,
        string title,
        Guid templateId,
        string workingCopyKey,
        string author)
    {
        ArgumentNullException.ThrowIfNull(parent);

        if (parent.IsAnnexure)
        {
            throw new InvalidOperationException(
                $"{parent.DocumentNumber} is itself an annexure. Annexures cannot be nested — "
                + "an annexure belongs to exactly one parent document.");
        }

        if (annexureNumber < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(annexureNumber), annexureNumber, "Annexure numbering starts at 1.");
        }

        return new ControlledDocument(
            $"{parent.DocumentNumber}-A{annexureNumber}",
            title,
            parent.SiteId,
            parent.DepartmentId,
            parent.DocumentTypeId,
            templateId,
            workingCopyKey,
            author)
        {
            ParentDocumentId = parent.Id,
            AnnexureNumber = annexureNumber,
            Revision = parent.Revision,
            Status = parent.Status,
        };
    }

    /// <summary>Issued once at creation and never reissued — the identity a controlled copy is traced by.</summary>
    public string DocumentNumber { get; private set; } = "";

    public string Title { get; private set; } = "";

    public Guid SiteId { get; private set; }
    public Guid DepartmentId { get; private set; }
    public Guid DocumentTypeId { get; private set; }

    /// <summary>The exact template version this document was created from. Never updated.</summary>
    public Guid TemplateId { get; private set; }

    /// <summary>Key into the document store for the editable working copy.</summary>
    public string WorkingCopyKey { get; private set; } = "";

    /// <summary>0 for first issue. Incremented only when a revision cycle completes.</summary>
    public int Revision { get; private set; }

    /// <summary>
    /// The SOP this annexure belongs to, or null for a document that stands on its own.
    /// <para>
    /// An annexure is a controlled document in its own right — its own number, its own file,
    /// its own controlled copies — but it is <b>not separately approvable</b>. It is signed,
    /// issued and withdrawn as part of its parent's lifecycle, never on its own. Every
    /// lifecycle method below refuses to act directly on an annexure for that reason; the
    /// parent moves, and the annexures move with it.
    /// </para>
    /// <para>
    /// Points at a specific revision rather than the family: annexure 1 of revision 2 is a
    /// different document from annexure 1 of revision 1, and binding to the family would make
    /// it ambiguous which revision's form an operator should be holding.
    /// </para>
    /// </summary>
    public Guid? ParentDocumentId { get; private set; }

    /// <summary>
    /// Position among its parent's annexures — 1, 2, 3 — driving both the number suffix and
    /// the order they print in. Null for a document that isn't an annexure.
    /// </summary>
    public int? AnnexureNumber { get; private set; }

    /// <summary>True when this document is an annexure to another.</summary>
    public bool IsAnnexure => ParentDocumentId is not null;

    /// <summary>
    /// Identifies the lineage this document belongs to — every revision of the same controlled
    /// document shares it. Revision 0 uses its own <see cref="Entity.Id"/>.
    /// </summary>
    public Guid FamilyId { get; private set; }

    /// <summary>
    /// Whether this is the revision currently in force, or the latest one if none is yet.
    /// <para>
    /// Exactly one row per family carries this. It's what the master list filters on, and what
    /// title uniqueness is enforced against — a superseded Rev 00 and a live Rev 01 share a
    /// title, and only the live one should collide with anything.
    /// </para>
    /// <para>
    /// Set false on a draft revision while its predecessor is still in force: the old version
    /// remains the current one until the new one is actually issued, which is exactly what a
    /// user asking "which SOP do I follow today" needs to be told.
    /// </para>
    /// </summary>
    public bool IsCurrentRevision { get; private set; }

    public DocumentStatus Status { get; private set; } = DocumentStatus.Draft;

    public string Author { get; private set; } = "";

    /// <summary>Set when the document becomes effective. Null while it's still in draft or review.</summary>
    public DateOnly? EffectiveDate { get; private set; }

    /// <summary>
    /// Frozen copy of the content as it stood when the last approver signed. Separate key from
    /// <see cref="WorkingCopyKey"/> so the approved artefact is immutable regardless of what
    /// happens to the working copy afterwards.
    /// </summary>
    public string? ApprovedCopyKey { get; private set; }

    /// <summary>
    /// SHA-256 of the approved content. 21 CFR Part 11 §11.70 requires signatures to be linked
    /// to their records such that they can't be excised and transplanted onto another record;
    /// every signature stores this same hash, so a substituted file no longer matches what was
    /// signed.
    /// </summary>
    public string? ApprovedContentHash { get; private set; }

    public DateTimeOffset? SubmittedAt { get; private set; }

    /// <summary>
    /// When this document must next be re-reviewed. Set at issuance from the type's review
    /// policy, and pushed forward each time a periodic review concludes no change is needed.
    /// Null when no policy applies — some document types genuinely never expire.
    /// </summary>
    public DateOnly? NextReviewDate { get; private set; }

    public DateTimeOffset? LastReviewedAt { get; private set; }

    public string? LastReviewedBy { get; private set; }

    /// <summary>Why the document was withdrawn from use. Required when obsoleting.</summary>
    public string? ObsoleteReason { get; private set; }

    /// <summary>
    /// Date this record becomes eligible for disposition. Set when the document leaves active
    /// use — superseded or obsoleted — from the type's retention policy. Null while in use, or
    /// when no policy applies.
    /// </summary>
    public DateOnly? RetainUntil { get; private set; }

    /// <summary>
    /// When the stored file was destroyed under an approved disposition. The register row,
    /// its signatures and its audit trail survive — a retention period permits destroying the
    /// document, not the evidence that it existed and was controlled.
    /// </summary>
    public DateTimeOffset? ContentDestroyedAt { get; private set; }

    /// <summary>Set once a disposition decision is recorded, removing it from the worklist.</summary>
    public DispositionAction? Disposition { get; private set; }

    public string? DispositionBy { get; private set; }

    public string? DispositionNote { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    /// <summary>Only a Draft is editable by its author; everything later is read-only to them.</summary>
    public bool IsEditable => Status == DocumentStatus.Draft;

    /// <summary>
    /// Renaming a draft is allowed — a title is still being settled at that stage, and the
    /// document number, not the title, is the identity. Once the document leaves Draft the
    /// title is part of an issued record and this throws rather than silently doing nothing.
    /// </summary>
    public void Retitle(string title)
    {
        if (Status != DocumentStatus.Draft)
        {
            throw new InvalidOperationException(
                $"Cannot retitle {DocumentNumber}: status is {Status}, not {DocumentStatus.Draft}.");
        }

        Title = RequireNonEmpty(title, nameof(title)).Trim();
        Touch();
    }

    /// <summary>
    /// Abandons a draft. Terminal, and deliberately not a delete: the number stays issued and
    /// the record stays in the register, because a controlled-document number that silently
    /// vanishes is a gap an auditor will ask about.
    /// </summary>
    public void Withdraw()
    {
        RefuseIfAnnexure(nameof(Withdraw));

        if (Status != DocumentStatus.Draft)
        {
            throw new InvalidOperationException(
                $"Cannot withdraw {DocumentNumber}: only a {DocumentStatus.Draft} document can be withdrawn "
                + $"at this stage of the build; status is {Status}.");
        }

        Status = DocumentStatus.Withdrawn;
        Touch();
    }

    /// <summary>
    /// Locks the draft and starts the signature route. From here the author can no longer edit
    /// — <see cref="IsEditable"/> goes false — which is what makes the content hash recorded
    /// against each signature meaningful.
    /// </summary>
    public void SubmitForReview()
    {
        RefuseIfAnnexure(nameof(SubmitForReview));

        if (Status != DocumentStatus.Draft)
        {
            throw new InvalidOperationException(
                $"Cannot submit {DocumentNumber}: status is {Status}, not {DocumentStatus.Draft}.");
        }

        SubmittedAt = DateTimeOffset.UtcNow;
        Status = DocumentStatus.InReview;
        Touch();
    }

    /// <summary>
    /// A reviewer rejected it. Returns to Draft and editable. Signatures already collected on
    /// the route are not deleted — they are a record of what those people saw and decided, and
    /// the route is superseded rather than erased.
    /// </summary>
    public void ReturnForRework()
    {
        if (Status != DocumentStatus.InReview)
        {
            throw new InvalidOperationException(
                $"Cannot return {DocumentNumber} for rework: status is {Status}, not {DocumentStatus.InReview}.");
        }

        Status = DocumentStatus.Draft;
        Touch();
    }

    /// <summary>
    /// Every step on the route has been signed. Approved is not yet in force; issuance is a
    /// separate, dated act.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> guarded against annexures, unlike the user-initiated transitions.
    /// An annexure is a separate file and needs its own frozen copy and content hash — the
    /// §11.70 binding is per-file, so a signature covering the parent alone would leave the
    /// annexure's contents unattested. The workflow service calls this once per document when
    /// the parent's route completes: parent first, then each annexure.
    /// </remarks>
    public void MarkApproved(string approvedCopyKey, string approvedContentHash)
    {
        if (Status != DocumentStatus.InReview)
        {
            throw new InvalidOperationException(
                $"Cannot approve {DocumentNumber}: status is {Status}, not {DocumentStatus.InReview}.");
        }

        ApprovedCopyKey = RequireNonEmpty(approvedCopyKey, nameof(approvedCopyKey));
        ApprovedContentHash = RequireNonEmpty(approvedContentHash, nameof(approvedContentHash));
        Status = DocumentStatus.Approved;
        Touch();
    }

    /// <summary>
    /// Brings the document into force on a given date.
    /// <para>
    /// A future date is allowed and is the normal case — training and distribution have to
    /// happen before an SOP takes effect. A past date is not: backdating when something came
    /// into force is precisely the kind of record an inspector treats as a finding.
    /// </para>
    /// </summary>
    public void MakeEffective(DateOnly effectiveDate, DateOnly today, DateOnly? nextReviewDate = null)
    {
        RefuseIfAnnexure(nameof(MakeEffective));

        if (Status != DocumentStatus.Approved)
        {
            throw new InvalidOperationException(
                $"Cannot issue {DocumentNumber}: status is {Status}, not {DocumentStatus.Approved}.");
        }

        if (effectiveDate < today)
        {
            throw new InvalidOperationException(
                $"Effective date {effectiveDate:O} is in the past; a document cannot be backdated into force.");
        }

        EffectiveDate = effectiveDate;
        NextReviewDate = nextReviewDate;
        Status = DocumentStatus.Effective;
        Touch();
    }

    /// <summary>
    /// Records that a periodic review happened and the document was found still correct,
    /// pushing the due date out by another interval.
    /// <para>
    /// Only meaningful for a document actually in force. Reviewing a superseded version is a
    /// contradiction — the thing to review is whatever replaced it.
    /// </para>
    /// </summary>
    public void RecordPeriodicReview(DateOnly nextReviewDate, string reviewedBy)
    {
        if (Status != DocumentStatus.Effective)
        {
            throw new InvalidOperationException(
                $"Cannot record a periodic review for {DocumentNumber}: status is {Status}, "
                + $"not {DocumentStatus.Effective}.");
        }

        NextReviewDate = nextReviewDate;
        LastReviewedAt = DateTimeOffset.UtcNow;
        LastReviewedBy = string.IsNullOrWhiteSpace(reviewedBy)
            ? throw new ArgumentException("Reviews must be attributable.", nameof(reviewedBy))
            : reviewedBy;
        Touch();
    }

    /// <summary>Replaced by a later revision that is now in force.</summary>
    /// <remarks>
    /// Not guarded against annexures for the same reason as <see cref="MarkApproved"/>: this is
    /// reached through a parent's supersession, not by a user acting on an annexure directly.
    /// </remarks>
    public void Supersede()
    {
        if (Status != DocumentStatus.Effective)
        {
            throw new InvalidOperationException(
                $"Cannot supersede {DocumentNumber}: status is {Status}, not {DocumentStatus.Effective}.");
        }

        Status = DocumentStatus.Superseded;
        Touch();
    }

    /// <summary>
    /// Withdrawn from use with no replacement. Retained, not deleted — the retention clock
    /// starts here rather than the record disappearing.
    /// </summary>
    public void MakeObsolete(string reason)
    {
        RefuseIfAnnexure(nameof(MakeObsolete));

        if (Status is not (DocumentStatus.Effective or DocumentStatus.Superseded))
        {
            throw new InvalidOperationException(
                $"Cannot obsolete {DocumentNumber}: status is {Status}.");
        }

        ObsoleteReason = string.IsNullOrWhiteSpace(reason)
            ? throw new ArgumentException("A reason is required to withdraw a document from use.", nameof(reason))
            : reason.Trim();

        // The review clock stops. Leaving a due date on a withdrawn document would keep it
        // surfacing in the overdue report forever, which is how real overdue items get lost.
        NextReviewDate = null;
        Status = DocumentStatus.Obsolete;
        Touch();
    }

    /// <summary>
    /// Starts the next revision as a separate record, keeping the same document number and
    /// lineage.
    /// <para>
    /// A new row rather than mutating this one, deliberately. The effective version's content,
    /// its signatures and the hash binding them stay intact and retrievable — a revision that
    /// overwrote its predecessor would destroy the record of what was actually in force last
    /// year, which is the single thing a retrospective investigation most needs.
    /// </para>
    /// <para>
    /// The new revision is <b>not</b> current until it is issued. Until then this document
    /// remains the one in force.
    /// </para>
    /// </summary>
    public ControlledDocument BeginRevision(string workingCopyKey, string author)
    {
        RefuseIfAnnexure(nameof(BeginRevision));

        if (Status != DocumentStatus.Effective)
        {
            throw new InvalidOperationException(
                $"Cannot revise {DocumentNumber}: status is {Status}, not {DocumentStatus.Effective}. "
                + "Only the version currently in force can be revised.");
        }

        return new ControlledDocument(
            DocumentNumber,
            Title,
            SiteId,
            DepartmentId,
            DocumentTypeId,
            TemplateId,
            workingCopyKey,
            author)
        {
            Revision = Revision + 1,
            FamilyId = FamilyId,
            IsCurrentRevision = false,
        };
    }

    /// <summary>
    /// Marks this revision as the one in force. Called when it is issued, after its
    /// predecessor has been superseded — the two happen in one transaction so the family is
    /// never left with two current revisions or none.
    /// </summary>
    public void PromoteToCurrent()
    {
        IsCurrentRevision = true;
        Touch();
    }

    /// <summary>Steps aside for a successor that has just taken effect.</summary>
    public void StandDown()
    {
        IsCurrentRevision = false;
        Touch();
    }

    /// <summary>
    /// Starts the retention clock. Called when the document leaves active use, by the service
    /// that resolved the applicable policy.
    /// <para>
    /// Idempotent in the sense that it only ever moves the date forward from an unset state —
    /// a record whose clock already started must not have it silently reset by a later event,
    /// or its retention would extend every time something touched it.
    /// </para>
    /// </summary>
    public void StartRetention(DateOnly retainUntil)
    {
        RetainUntil ??= retainUntil;
        Touch();
    }

    /// <summary>
    /// Records the decision taken when retention expired.
    /// <para>
    /// Deliberately separate from actually deleting the file: this marks what was decided and
    /// by whom, and the service performs the deletion afterwards. Destruction of a controlled
    /// record is an authorised act with a name against it, not a background sweep.
    /// </para>
    /// </summary>
    public void RecordDisposition(DispositionAction action, string note, string decidedBy)
    {
        if (Status is not (DocumentStatus.Superseded or DocumentStatus.Obsolete))
        {
            throw new InvalidOperationException(
                $"Cannot dispose of {DocumentNumber}: status is {Status}. Only a record that has "
                + "left active use can be dispositioned.");
        }

        if (Disposition is { } existing)
        {
            throw new InvalidOperationException(
                $"{DocumentNumber} Rev {Revision:00} was already dispositioned as {existing}.");
        }

        Disposition = action;
        DispositionNote = string.IsNullOrWhiteSpace(note)
            ? throw new ArgumentException("A disposition decision needs a recorded rationale.", nameof(note))
            : note.Trim();
        DispositionBy = string.IsNullOrWhiteSpace(decidedBy)
            ? throw new ArgumentException("Disposition must be attributable.", nameof(decidedBy))
            : decidedBy;

        if (action == DispositionAction.DestroyContent)
        {
            ContentDestroyedAt = DateTimeOffset.UtcNow;
        }

        Touch();
    }

    private void Touch() => UpdatedAt = DateTimeOffset.UtcNow;

    /// <summary>
    /// Refuses a lifecycle operation attempted directly on an annexure.
    /// <para>
    /// The whole invariant in one place: an annexure is signed, issued and withdrawn as part of
    /// its parent's lifecycle and never on its own. Allowing a direct transition would let an
    /// annexure become effective while its parent was still in review — a form in circulation
    /// for a procedure that isn't in force, which is precisely the failure a controlled-document
    /// system exists to prevent.
    /// </para>
    /// </summary>
    private void RefuseIfAnnexure(string operation)
    {
        if (IsAnnexure)
        {
            throw new InvalidOperationException(
                $"{operation} cannot be performed directly on {DocumentNumber}: it is an "
                + "annexure. Perform the operation on its parent document, and the annexure "
                + "will follow.");
        }
    }

    /// <summary>
    /// Moves this annexure to match its parent. The only way an annexure's status ever changes.
    /// <para>
    /// Deliberately assigns rather than validating a transition path: the parent has already
    /// enforced which transitions are legal, and re-deriving that here would mean two sets of
    /// rules that could disagree. What this does enforce is that it is only ever called on an
    /// annexure, and only by the parent.
    /// </para>
    /// </summary>
    /// <param name="parent">Passed so the caller cannot cascade from an unrelated document.</param>
    public void FollowParent(ControlledDocument parent)
    {
        ArgumentNullException.ThrowIfNull(parent);

        if (!IsAnnexure)
        {
            throw new InvalidOperationException(
                $"{DocumentNumber} is not an annexure and has no parent to follow.");
        }

        if (parent.Id != ParentDocumentId)
        {
            throw new InvalidOperationException(
                $"{DocumentNumber} is an annexure of a different document, not {parent.DocumentNumber}.");
        }

        Status = parent.Status;
        EffectiveDate = parent.EffectiveDate;
        NextReviewDate = parent.NextReviewDate;
        ObsoleteReason = parent.ObsoleteReason;
        IsCurrentRevision = parent.IsCurrentRevision;

        Touch();
    }

    private static string RequireNonEmpty(string value, string paramName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{paramName} is required.", paramName)
            : value;
}
