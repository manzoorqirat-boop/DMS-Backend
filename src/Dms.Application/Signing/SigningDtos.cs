using Dms.Domain.Entities;
using Dms.Domain.Enums;

namespace Dms.Application.Signing;

/// <summary>
/// A submission. The route's shape comes from the configured workflow definition; all the
/// submitter supplies is who fills each slot, keyed by step order.
/// </summary>
public sealed record SubmitForReviewRequest(IReadOnlyList<Dms.Application.Workflows.RouteNomination> Nominations);

/// <summary>
/// A signing action. <see cref="Password"/> is the §11.200 second component and is never
/// stored, logged, or echoed back — it is verified and discarded.
/// </summary>
public sealed record SignRequest(string Password, SignatureMeaning Meaning, string? Reason);

/// <summary>
/// An applied signature as displayed. Carries the printed name, timestamp and meaning that
/// §11.50(a) requires appear with it — assembled here so no caller has to remember to.
/// </summary>
public sealed record SignatureView(
    string UserName,
    string FullName,
    string Department,
    string Designation,
    SignatureMeaning Meaning,
    DateTimeOffset SignedAt,
    string StepLabel,
    int StepOrder,
    string ContentHash,
    string? Reason)
{
    public static SignatureView From(ElectronicSignature signature, SignatureRequest step) => new(
        signature.UserName,
        signature.FullName,
        signature.Department,
        signature.Designation,
        signature.Meaning,
        signature.SignedAt,
        step.StepLabel,
        step.StepOrder,
        signature.ContentHash,
        signature.Reason);
}

public sealed record RouteStepView(
    int StepOrder,
    string StepLabel,
    string UserName,
    SignatureRole Role,
    SignatureRequestStatus Status,
    SignatureView? Signature);

public sealed record RouteView(
    Guid DocumentId,
    string DocumentNumber,
    DocumentStatus DocumentStatus,
    string? ApprovedContentHash,
    IReadOnlyList<RouteStepView> Steps);

public sealed record PendingSignatureView(
    Guid DocumentId,
    string DocumentNumber,
    string Title,
    int StepOrder,
    string StepLabel,
    SignatureRole Role,
    DateTimeOffset? SubmittedAt);

public sealed record DocumentIssueView(string DocumentNumber, DocumentStatus Status, DateOnly? EffectiveDate);

public sealed record CreateUserRequest(
    string UserName,
    string FullName,
    string Department,
    string Designation,
    string Password);

public sealed record UserSummary(
    Guid Id,
    string UserName,
    string FullName,
    string Department,
    string Designation,
    bool IsActive,
    bool IsLockedOut)
{
    public static UserSummary From(DmsUser user, DateTimeOffset now) => new(
        user.Id,
        user.UserName,
        user.FullName,
        user.Department,
        user.Designation,
        user.IsActive,
        user.IsLockedOut(now));
}
