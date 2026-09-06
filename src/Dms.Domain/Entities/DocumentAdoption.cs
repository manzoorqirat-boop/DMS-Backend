using Dms.Domain.Common;

namespace Dms.Domain.Entities;

/// <summary>
/// A site's formal adoption of a globally-issued document.
/// <para>
/// The point of this record is the question an inspector asks: <i>this SOP was effective at
/// your site from March — who at your site said so?</i> Without an adoption there is no
/// answer, because the document appeared from corporate and nobody local ever accepted it.
/// With one there is a named person, a date, and a signature.
/// </para>
/// <para>
/// <b>It holds no content.</b> The adopting site takes the document verbatim and has no
/// authority to alter it — so there is one document and many adoptions, not one document per
/// site. That also means a revision at the owning site cannot silently diverge from what a
/// site is following: there is only ever one text.
/// </para>
/// <para>
/// The effective date is the <i>site's own</i>, and usually later than the owning site's. The
/// gap is where local training and any local practice changes happen, and pretending it does
/// not exist — by making a global document effective everywhere the moment it is issued — is
/// the failure this whole mechanism is built to avoid.
/// </para>
/// </summary>
public class DocumentAdoption : Entity, ITimestamped
{
    private DocumentAdoption() { }

    public DocumentAdoption(
        Guid documentId,
        Guid siteId,
        DateOnly effectiveDate,
        DateOnly today,
        string adoptedBy,
        string? note)
    {
        if (effectiveDate < today)
        {
            // Backdating would let a site claim staff were working to a procedure before it
            // said they were. Forward-dating is fine and normal — that is the training window.
            throw new ArgumentException(
                "An adoption cannot take effect in the past. Adoption is the site accepting the "
                + "document from that date forward.",
                nameof(effectiveDate));
        }

        DocumentId = documentId;
        SiteId = siteId;
        EffectiveDate = effectiveDate;
        AdoptedBy = RequireNonEmpty(adoptedBy, nameof(adoptedBy));
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        AdoptedAt = DateTimeOffset.UtcNow;
        CreatedAt = AdoptedAt;
        IsActive = true;
    }

    public Guid DocumentId { get; private set; }
    public Guid SiteId { get; private set; }

    /// <summary>When the document takes effect at this site. Never earlier than the adoption.</summary>
    public DateOnly EffectiveDate { get; private set; }

    public string AdoptedBy { get; private set; } = "";
    public DateTimeOffset AdoptedAt { get; private set; }
    public string? Note { get; private set; }

    /// <summary>
    /// False once the site stops following the document — either because it withdrew locally,
    /// or because the revision it adopted was superseded and the new one needs adopting afresh.
    /// </summary>
    public bool IsActive { get; private set; }

    public string? WithdrawnBy { get; private set; }
    public DateTimeOffset? WithdrawnAt { get; private set; }
    public string? WithdrawalReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    /// <summary>Whether the document is actually in force at this site on a given day.</summary>
    public bool IsInForceOn(DateOnly date) => IsActive && EffectiveDate <= date;

    /// <summary>
    /// The site stops following the document.
    /// <para>
    /// Also used when the adopted revision is superseded: the adoption ends rather than
    /// silently rolling forward, because a site adopting Rev 02 has not thereby accepted Rev 03
    /// — the changes are exactly what it needs to review and train against.
    /// </para>
    /// </summary>
    public void Withdraw(string withdrawnBy, string reason)
    {
        if (!IsActive)
        {
            throw new InvalidOperationException("This adoption has already been withdrawn.");
        }

        WithdrawnBy = RequireNonEmpty(withdrawnBy, nameof(withdrawnBy));
        WithdrawalReason = RequireNonEmpty(reason, nameof(reason));
        WithdrawnAt = DateTimeOffset.UtcNow;
        IsActive = false;

        UpdatedAt = WithdrawnAt;
    }

    private static string RequireNonEmpty(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{name} is required.", name)
            : value.Trim();
}
