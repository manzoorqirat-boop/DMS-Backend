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

    public DocumentStatus Status { get; private set; } = DocumentStatus.Draft;

    public string Author { get; private set; } = "";

    /// <summary>Set when the document becomes effective. Null while it's still in draft or review.</summary>
    public DateOnly? EffectiveDate { get; private set; }

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

    private void Touch() => UpdatedAt = DateTimeOffset.UtcNow;

    private static string RequireNonEmpty(string value, string paramName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{paramName} is required.", paramName)
            : value;
}
