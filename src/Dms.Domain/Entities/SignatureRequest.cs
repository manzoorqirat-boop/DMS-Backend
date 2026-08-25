using Dms.Domain.Common;
using Dms.Domain.Enums;

namespace Dms.Domain.Entities;

/// <summary>
/// One step on a document's signing route: who must act, in what capacity, and whether they
/// have.
/// <para>
/// Steps run in strict order — only the lowest-numbered <see cref="SignatureRequestStatus.Pending"/>
/// step is ever signable, which is what <see cref="Dms.Application.Signing.ReviewWorkflowService"/>
/// enforces and what makes the route sequential rather than a free-for-all. This entity itself
/// enforces the narrower rule: a step can only ever leave <c>Pending</c> once.
/// </para>
/// <para>
/// <b>Reconstructed file.</b> Present in the working codebase before this review but absent
/// from the uploaded archive. Rebuilt from the calling contract in
/// <c>ISignatureRepository</c>, <c>SigningRepositories.cs</c> and <c>ReviewWorkflowService.cs</c>,
/// all of which were present and specify its constructor, properties and transition methods
/// unambiguously. Anything not exercised by that calling code — internal field names, for
/// instance — may differ from the original.
/// </para>
/// </summary>
public class SignatureRequest : Entity
{
    private SignatureRequest() { }

    public SignatureRequest(
        Guid documentId,
        int stepOrder,
        Guid userId,
        string userName,
        SignatureRole role,
        string stepLabel)
    {
        DocumentId = documentId;
        StepOrder = stepOrder > 0
            ? stepOrder
            : throw new ArgumentOutOfRangeException(nameof(stepOrder), "Step order starts at 1.");
        UserId = userId;
        UserName = string.IsNullOrWhiteSpace(userName)
            ? throw new ArgumentException("Signatory username is required.", nameof(userName))
            : userName;
        Role = role;
        StepLabel = string.IsNullOrWhiteSpace(stepLabel)
            ? throw new ArgumentException("Step label is required.", nameof(stepLabel))
            : stepLabel.Trim();
        Status = SignatureRequestStatus.Pending;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid DocumentId { get; private set; }

    public int StepOrder { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>
    /// Denormalised from the user record at route-creation time. Kept even though
    /// <see cref="UserId"/> is the real reference, so the route reads correctly without a join
    /// even if the account is later renamed or deactivated.
    /// </summary>
    public string UserName { get; private set; } = "";

    public SignatureRole Role { get; private set; }

    /// <summary>"Reviewed By", "Approved By" — what prints against the signature.</summary>
    public string StepLabel { get; private set; } = "";

    public SignatureRequestStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The step was signed. Terminal.</summary>
    public void MarkSigned()
    {
        RequirePending();
        Status = SignatureRequestStatus.Signed;
    }

    /// <summary>The signatory rejected the document at this step. Terminal.</summary>
    public void MarkRejected()
    {
        RequirePending();
        Status = SignatureRequestStatus.Rejected;
    }

    /// <summary>
    /// The route was abandoned before this step was reached — an earlier rejection, or the
    /// document withdrawn. Distinct from a rejection: this signatory never saw the document,
    /// so recording anything else here would attribute a decision nobody made.
    /// </summary>
    public void Cancel()
    {
        RequirePending();
        Status = SignatureRequestStatus.Cancelled;
    }

    private void RequirePending()
    {
        if (Status != SignatureRequestStatus.Pending)
        {
            throw new InvalidOperationException(
                $"Step {StepOrder} is {Status} and cannot be transitioned again.");
        }
    }
}
