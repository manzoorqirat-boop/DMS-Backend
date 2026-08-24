using Dms.Application.Abstractions;
using Dms.Application.Common;
using Dms.Domain.Entities;
using Dms.Domain.Enums;

namespace Dms.Application.Workflows;

/// <summary>
/// Administers review-route definitions, and resolves the concrete route a given document will
/// follow.
/// <para>
/// The split matters: <see cref="CreateAsync"/> and friends are configuration, gated on
/// <see cref="Permission.WorkflowConfigure"/>. <see cref="ResolveTemplateAsync"/> is what a
/// submitter calls to see the route their document must take and who is eligible to fill each
/// slot — they cannot change its shape, only pick people within it.
/// </para>
/// </summary>
public sealed class WorkflowDefinitionService(
    IWorkflowDefinitionRepository definitions,
    IRoleRepository roles,
    IControlledDocumentRepository documents,
    IAccessControl access,
    IAuditTrail audit,
    ICurrentUser currentUser)
{
    private const string EntityType = "WorkflowDefinition";

    public async Task<Result<WorkflowView>> CreateAsync(
        CreateWorkflowRequest request,
        CancellationToken cancellationToken)
    {
        var gate = await RequireConfigureAsync(request.SiteId, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var knownRoles = await roles.ListAsync(includeInactive: false, cancellationToken);
        var roleLookup = knownRoles.ToDictionary(r => r.Id, r => r.Code);

        var unknown = request.Steps.Where(s => !roleLookup.ContainsKey(s.RoleId)).ToList();
        if (unknown.Count > 0)
        {
            // Caught at configuration time rather than at submission: a route naming a role
            // that doesn't exist would fail for the first author who tried to use it.
            return Error.Validation(
                "workflow_role_unknown",
                $"{unknown.Count} step(s) name a role that doesn't exist or is deactivated.");
        }

        WorkflowDefinition definition;
        try
        {
            definition = new WorkflowDefinition(
                request.DocumentTypeId, request.SiteId, request.Name, currentUser.UserName!);
            definition.SetSteps(request.Steps.Select(s => (s.RoleId, s.Role, s.StepLabel)));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Error.Validation("workflow_invalid", ex.Message);
        }

        definitions.Add(definition);
        audit.Record(
            AuditAction.WorkflowDefinitionCreated, EntityType, definition.Id, definition.Name,
            DescribeRoute(definition, roleLookup));

        var outcome = await definitions.SaveChangesAsync(cancellationToken);
        return outcome.Saved
            ? WorkflowView.From(definition, roleLookup)
            : Error.Conflict("workflow_save_conflict", "The workflow could not be saved.");
    }

    public async Task<Result<WorkflowView>> SetStepsAsync(
        Guid definitionId,
        IReadOnlyList<WorkflowStepRequest> steps,
        CancellationToken cancellationToken)
    {
        var definition = await definitions.GetAsync(definitionId, cancellationToken);
        if (definition is null)
        {
            return Error.NotFound("workflow_not_found", $"No workflow definition with id {definitionId}.");
        }

        var gate = await RequireConfigureAsync(definition.SiteId, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var knownRoles = await roles.ListAsync(includeInactive: false, cancellationToken);
        var roleLookup = knownRoles.ToDictionary(r => r.Id, r => r.Code);

        try
        {
            definition.SetSteps(steps.Select(s => (s.RoleId, s.Role, s.StepLabel)));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Error.Validation("workflow_invalid", ex.Message);
        }

        audit.Record(
            AuditAction.WorkflowStepsChanged, EntityType, definition.Id, definition.Name,
            $"v{definition.Version}: {DescribeRoute(definition, roleLookup)} "
            + "Documents already in review keep the route they were submitted under.");

        var outcome = await definitions.SaveChangesAsync(cancellationToken);
        return outcome.Saved
            ? WorkflowView.From(definition, roleLookup)
            : Error.Conflict("workflow_save_conflict", "The workflow could not be updated.");
    }

    public async Task<Result<WorkflowView>> SetActiveAsync(
        Guid definitionId,
        bool isActive,
        CancellationToken cancellationToken)
    {
        var definition = await definitions.GetAsync(definitionId, cancellationToken);
        if (definition is null)
        {
            return Error.NotFound("workflow_not_found", $"No workflow definition with id {definitionId}.");
        }

        var gate = await RequireConfigureAsync(definition.SiteId, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        try
        {
            if (isActive)
            {
                definition.Activate();
            }
            else
            {
                definition.Deactivate();
            }
        }
        catch (InvalidOperationException ex)
        {
            return Error.Conflict("workflow_not_activatable", ex.Message);
        }

        audit.Record(
            isActive ? AuditAction.WorkflowActivated : AuditAction.WorkflowDeactivated,
            EntityType, definition.Id, definition.Name, $"Version {definition.Version}.");

        var outcome = await definitions.SaveChangesAsync(cancellationToken);
        if (!outcome.Saved)
        {
            return outcome.ViolatedIndexContains("active")
                ? Error.Conflict(
                    "workflow_already_active",
                    "Another workflow is already active for that document type and site.")
                : Error.Conflict("workflow_save_conflict", "The workflow could not be updated.");
        }

        var knownRoles = await roles.ListAsync(includeInactive: true, cancellationToken);
        return WorkflowView.From(definition, knownRoles.ToDictionary(r => r.Id, r => r.Code));
    }

    public async Task<IReadOnlyList<WorkflowView>> ListAsync(
        Guid? documentTypeId,
        CancellationToken cancellationToken)
    {
        var found = await definitions.ListAsync(documentTypeId, cancellationToken);
        var knownRoles = await roles.ListAsync(includeInactive: true, cancellationToken);
        var roleLookup = knownRoles.ToDictionary(r => r.Id, r => r.Code);

        return found.Select(d => WorkflowView.From(d, roleLookup)).ToList();
    }

    /// <summary>
    /// The route a document must follow, with eligible people resolved per step.
    /// <para>
    /// Candidates are the holders of the step's role at <i>this document's</i> site and
    /// department. A step with no eligible holder is returned with an empty candidate list
    /// rather than being silently dropped — a route that can't be filled is a configuration
    /// problem someone needs to see, not one to route around.
    /// </para>
    /// </summary>
    public async Task<Result<RouteTemplateView>> ResolveTemplateAsync(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var document = await documents.GetAsync(documentId, cancellationToken);
        if (document is null)
        {
            return Error.NotFound("document_not_found", $"No document with id {documentId}.");
        }

        var definition = await ResolveDefinitionAsync(
            document.DocumentTypeId, document.SiteId, cancellationToken);

        if (definition is null)
        {
            return Error.Conflict(
                "no_active_workflow",
                "No active review route is configured for this document type. Configure one before submitting.");
        }

        var knownRoles = await roles.ListAsync(includeInactive: true, cancellationToken);
        var roleLookup = knownRoles.ToDictionary(r => r.Id, r => r.Code);

        var slots = new List<RouteSlot>();

        foreach (var step in definition.Steps)
        {
            var candidates = await CandidatesForAsync(step, document, cancellationToken);

            slots.Add(new RouteSlot(
                step.StepOrder,
                step.StepLabel,
                step.Role,
                step.RoleId,
                roleLookup.GetValueOrDefault(step.RoleId, ""),
                candidates));
        }

        return Result<RouteTemplateView>.Success(new RouteTemplateView(
            document.Id,
            document.DocumentNumber,
            definition.Id,
            definition.Name,
            definition.Version,
            slots));
    }

    /// <summary>Most-specific-wins, same precedence as numbering rules.</summary>
    public async Task<WorkflowDefinition?> ResolveDefinitionAsync(
        Guid documentTypeId,
        Guid siteId,
        CancellationToken cancellationToken)
    {
        var candidates = await definitions.FindActiveCandidatesAsync(documentTypeId, siteId, cancellationToken);
        return candidates.OrderByDescending(d => d.Specificity).FirstOrDefault();
    }

    /// <summary>
    /// Users eligible to fill a step. Resolved through the permission the role grants rather
    /// than by role membership alone, so a role stripped of <see cref="Permission.DocumentSign"/>
    /// stops producing candidates immediately instead of producing people who would then be
    /// refused at signing time.
    /// </summary>
    private async Task<IReadOnlyList<CandidateView>> CandidatesForAsync(
        WorkflowStepDefinition step,
        ControlledDocument document,
        CancellationToken cancellationToken)
    {
        var holders = await access.UsersWithPermissionAsync(
            Permission.DocumentSign, document.SiteId, document.DepartmentId, cancellationToken);

        var assignments = await roles.ListAssignmentsAsync(
            userId: null, roleId: step.RoleId, cancellationToken);

        var eligibleUserIds = assignments
            .Where(a => a.AppliesTo(document.SiteId, document.DepartmentId))
            .Select(a => a.UserId)
            .ToHashSet();

        return holders
            .Where(u => eligibleUserIds.Contains(u.Id))
            .Select(u => new CandidateView(u.Id, u.UserName, u.FullName, u.Designation))
            .ToList();
    }

    private async Task<Error?> RequireConfigureAsync(Guid? siteId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentUser.UserName))
        {
            return Error.Validation("actor_unknown", "The acting user could not be determined.");
        }

        var allowed = await access.HasPermissionAsync(
            Permission.WorkflowConfigure, siteId, departmentId: null, cancellationToken);

        return allowed
            ? null
            : Error.Validation(
                "permission_denied",
                $"{Permission.WorkflowConfigure} is required at this scope to configure review routes.");
    }

    private static string DescribeRoute(
        WorkflowDefinition definition,
        IReadOnlyDictionary<Guid, string> roleCodes) =>
        definition.Steps.Count == 0
            ? "No steps."
            : string.Join(
                " → ",
                definition.Steps.Select(s =>
                    $"{s.StepOrder}. {roleCodes.GetValueOrDefault(s.RoleId, "?")} ({s.Role})"));
}
