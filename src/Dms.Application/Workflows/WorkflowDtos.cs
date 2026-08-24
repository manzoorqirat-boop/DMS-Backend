using Dms.Domain.Entities;
using Dms.Domain.Enums;

namespace Dms.Application.Workflows;

public sealed record CreateWorkflowRequest(
    Guid DocumentTypeId,
    Guid? SiteId,
    string Name,
    IReadOnlyList<WorkflowStepRequest> Steps);

public sealed record WorkflowStepRequest(Guid RoleId, SignatureRole Role, string StepLabel);

public sealed record WorkflowStepView(
    int StepOrder,
    Guid RoleId,
    string RoleCode,
    SignatureRole Role,
    string StepLabel);

public sealed record WorkflowView(
    Guid Id,
    Guid DocumentTypeId,
    Guid? SiteId,
    string Name,
    bool IsActive,
    int Version,
    string Scope,
    IReadOnlyList<WorkflowStepView> Steps)
{
    public static WorkflowView From(WorkflowDefinition definition, IReadOnlyDictionary<Guid, string> roleCodes) =>
        new(
            definition.Id,
            definition.DocumentTypeId,
            definition.SiteId,
            definition.Name,
            definition.IsActive,
            definition.Version,
            definition.SiteId is null ? "All sites" : "Site override",
            definition.Steps
                .Select(s => new WorkflowStepView(
                    s.StepOrder, s.RoleId, roleCodes.GetValueOrDefault(s.RoleId, ""), s.Role, s.StepLabel))
                .ToList());
}

/// <summary>
/// The route a specific document will follow, with the people eligible for each step already
/// resolved.
/// <para>
/// This is what a submission form renders: the shape is fixed by configuration, and for each
/// step the user picks from <see cref="RouteSlot.Candidates"/> rather than typing a username.
/// A step with exactly one candidate needs no choice at all.
/// </para>
/// </summary>
public sealed record RouteTemplateView(
    Guid DocumentId,
    string DocumentNumber,
    Guid WorkflowDefinitionId,
    string WorkflowName,
    int WorkflowVersion,
    IReadOnlyList<RouteSlot> Slots);

public sealed record RouteSlot(
    int StepOrder,
    string StepLabel,
    SignatureRole Role,
    Guid RoleId,
    string RoleCode,
    IReadOnlyList<CandidateView> Candidates);

public sealed record CandidateView(Guid UserId, string UserName, string FullName, string Designation);

/// <summary>A submitter's choice of who fills each slot. Validated against the definition.</summary>
public sealed record NominateRouteRequest(IReadOnlyList<RouteNomination> Nominations);

public sealed record RouteNomination(int StepOrder, string UserName);
