using Dms.Domain.Common;
using Dms.Domain.Enums;

namespace Dms.Domain.Entities;

/// <summary>
/// One applied electronic signature. Append-only: there is no mutator anywhere on this class,
/// by design — an amendable signature is not a signature. A decision that turns out to be wrong
/// is corrected by a new signature on a new revision, never by editing the old one.
/// <para>
/// Discharges three Part 11 obligations directly on the record: §11.50(a) requires the printed
/// name, timestamp and meaning to appear <i>with</i> the signature, so all three are stored
/// here rather than resolved live from the user table — a title change six months later must
/// not silently rewrite what an old approval says. §11.70 requires the signature be bound to
/// the record it signed, which is <see cref="ContentHash"/>. §11.10(e) requires the trail
/// itself be immutable, enforced a second time at <c>DmsDbContext.GuardAppendOnlyEntities</c>.
/// </para>
/// <para>
/// <b>Reconstructed file.</b> Present in the working codebase before this review but absent
/// from the uploaded archive. Rebuilt from the calling contract in <c>ISignatureRepository</c>,
/// <c>SigningRepositories.cs</c> (which orders by <see cref="SignedAt"/>) and
/// <c>ReviewWorkflowService.cs</c>/<c>SigningDtos.cs</c> (which fix the constructor's parameter
/// order and every property <c>SignatureView.From</c> reads). Anything not exercised by that
/// calling code may differ from the original.
/// </para>
/// </summary>
public class ElectronicSignature : Entity
{
    private ElectronicSignature() { }

    public ElectronicSignature(
        Guid documentId,
        Guid signatureRequestId,
        Guid userId,
        string userName,
        string fullName,
        string department,
        string designation,
        SignatureMeaning meaning,
        string contentHash,
        string? reason)
    {
        DocumentId = documentId;
        SignatureRequestId = signatureRequestId;
        UserId = userId;
        UserName = RequireNonEmpty(userName, nameof(userName));
        FullName = RequireNonEmpty(fullName, nameof(fullName));
        Department = RequireNonEmpty(department, nameof(department));
        Designation = RequireNonEmpty(designation, nameof(designation));
        Meaning = meaning;
        ContentHash = RequireNonEmpty(contentHash, nameof(contentHash));

        Reason = meaning == SignatureMeaning.Rejected
            ? (string.IsNullOrWhiteSpace(reason)
                ? throw new ArgumentException("A rejection requires a stated reason.", nameof(reason))
                : reason.Trim())
            : (string.IsNullOrWhiteSpace(reason) ? null : reason.Trim());

        SignedAt = DateTimeOffset.UtcNow;
    }

    public Guid DocumentId { get; private set; }

    /// <summary>The route step this signature discharges.</summary>
    public Guid SignatureRequestId { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>
    /// Printed name components, captured at the moment of signing rather than resolved from
    /// the live user record — §11.50(a)(1) requires the signature read as it did then.
    /// </summary>
    public string UserName { get; private set; } = "";

    public string FullName { get; private set; } = "";
    public string Department { get; private set; } = "";
    public string Designation { get; private set; } = "";

    /// <summary>What the signature means, displayed with it per §11.50(a)(3).</summary>
    public SignatureMeaning Meaning { get; private set; }

    /// <summary>Hash of the exact content signed. The §11.70 binding.</summary>
    public string ContentHash { get; private set; } = "";

    /// <summary>Required when <see cref="Meaning"/> is Rejected; optional otherwise.</summary>
    public string? Reason { get; private set; }

    public DateTimeOffset SignedAt { get; private set; }

    private static string RequireNonEmpty(string value, string paramName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{paramName} is required.", paramName)
            : value;
}
