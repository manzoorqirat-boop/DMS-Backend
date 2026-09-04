namespace Dms.Domain.Enums;

/// <summary>
/// Actions whose performance can be made to require an electronic signature.
/// <para>
/// Deliberately not every audited action. These are the ones where §11.10(b) and (e) bite
/// hardest: acts that change what is physically in circulation, or that destroy a record.
/// Reading a document or running a report is audited but needs no signature, and offering
/// every action here would bury the ones that matter.
/// </para>
/// <para>
/// Document approval is absent on purpose. That already has its own signature route through
/// <c>SignatureRequest</c> and <c>ElectronicSignature</c>, bound to a content hash — a
/// mechanism these action signatures deliberately do not disturb.
/// </para>
/// </summary>
public enum ControlledAction
{
    /// <summary>Issuing a controlled copy — putting paper into circulation.</summary>
    IssueCopy,

    /// <summary>Recording a copy as physically collected.</summary>
    RetrieveCopy,

    /// <summary>
    /// Closing a copy out as destroyed or lost. An unaccounted controlled copy is a finding,
    /// so writing one off is usually among the first actions a site wants countersigned.
    /// </summary>
    CloseOutCopy,

    /// <summary>Printing a controlled copy, which increments its print count.</summary>
    PrintCopy,

    /// <summary>Recording a periodic review that found a document still correct.</summary>
    PeriodicReview,

    /// <summary>Withdrawing a document from use.</summary>
    MakeObsolete,

    /// <summary>
    /// Destroying or permanently retaining a record after its retention period. Irreversible,
    /// and the strongest candidate for requiring approval <i>before</i> it takes effect rather
    /// than verification afterwards.
    /// </summary>
    RecordDisposition,
}

/// <summary>
/// When the second signature happens relative to the action taking effect.
/// <para>
/// The distinction is not cosmetic — it decides whether the act can occur while unsigned.
/// </para>
/// </summary>
public enum SecondSignatureTiming
{
    /// <summary>
    /// The action takes effect immediately; a second person confirms afterwards that it was
    /// performed correctly. This is second-person verification as a shop floor does it — the
    /// copy is handed over, then someone checks the register agrees.
    /// </summary>
    VerificationAfter,

    /// <summary>
    /// The action does not take effect until countersigned. Correct for anything irreversible:
    /// a record destroyed before approval cannot be un-destroyed when approval is refused.
    /// </summary>
    AuthorisationBefore,
}

/// <summary>Lifecycle of an action waiting on its remaining signature.</summary>
public enum PendingActionStatus
{
    /// <summary>Performed and signed once; waiting for a different person to countersign.</summary>
    AwaitingCountersignature,

    /// <summary>Fully signed. For AuthorisationBefore, this is when the effect was applied.</summary>
    Completed,

    /// <summary>The countersigner refused. The action does not stand.</summary>
    Rejected,

    /// <summary>Withdrawn by the person who requested it, before anyone countersigned.</summary>
    Cancelled,
}

/// <summary>
/// What an action signature asserts.
/// <para>
/// Separate from <see cref="SignatureMeaning"/>, which covers document approval. "Approved" on a
/// document means the content is correct; "Performed" on an action means someone did a thing.
/// Sharing one enum would let a document approval and a copy issuance carry the same meaning
/// string in the audit trail, which is exactly the ambiguity a manifest exists to remove.
/// </para>
/// </summary>
public enum ActionSignatureMeaning
{
    /// <summary>The first signature: "I performed this action."</summary>
    Performed,

    /// <summary>
    /// The countersignature under VerificationAfter: "I checked this was performed correctly."
    /// </summary>
    Verified,

    /// <summary>
    /// The countersignature under AuthorisationBefore: "I authorise this to be performed."
    /// </summary>
    Authorised,

    /// <summary>The countersigner declined. Requires a reason.</summary>
    Refused,
}
