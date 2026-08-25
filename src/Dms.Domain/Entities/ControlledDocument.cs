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

    private void Touch() => UpdatedAt = DateTimeOffset.UtcNow;

    private static string RequireNonEmpty(string value, string paramName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{paramName} is required.", paramName)
            : value;
}
