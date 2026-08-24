using Dms.Domain.Common;
using Dms.Domain.Enums;

namespace Dms.Domain.Entities;

/// <summary>
/// The review route for a document type — "QA reviews, then Head of Department reviews, then
/// Plant Head approves" — declared as master data instead of assembled by hand at every
/// submission.
/// <para>
/// A definition names <b>roles</b>, not people. Who specifically signs is resolved at
/// submission from the people holding that role at the document's own site and department,
/// which is what stops a route from silently breaking when someone leaves, and stops the
/// submitter from nominating a friendly approver who was never meant to be in the chain.
/// </para>
/// <para>
/// Changing a definition affects documents submitted <i>afterwards</i> only. A document in
/// flight has already had its route materialised into <see cref="SignatureRequest"/> rows, so
/// its chain is fixed at the moment of submission — which is the correct behaviour: the people
/// who agreed to review something must not change underneath them mid-review.
/// </para>
/// </summary>
public class WorkflowDefinition : Entity, ITimestamped
{
    private readonly List<WorkflowStepDefinition> _steps = [];

    private WorkflowDefinition() { }

    public WorkflowDefinition(Guid documentTypeId, Guid? siteId, string name, string createdBy)
    {
        DocumentTypeId = documentTypeId;
        SiteId = siteId;
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Workflow name is required.", nameof(name))
            : name.Trim();
        CreatedBy = string.IsNullOrWhiteSpace(createdBy)
            ? throw new ArgumentException("Workflow definitions must be attributable.", nameof(createdBy))
            : createdBy;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid DocumentTypeId { get; private set; }

    /// <summary>Null means this is the default route for the type, across all sites.</summary>
    public Guid? SiteId { get; private set; }

    public string Name { get; private set; } = "";

    public bool IsActive { get; private set; }

    /// <summary>
    /// Bumped on every step change. Not used to resolve anything — it exists so an audit entry
    /// can say which version of the route a document was submitted under, when the definition
    /// has since been edited.
    /// </summary>
    public int Version { get; private set; } = 1;

    public string CreatedBy { get; private set; } = "";

    public IReadOnlyList<WorkflowStepDefinition> Steps =>
        _steps.OrderBy(x => x.StepOrder).ToList();

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    /// <summary>How specific this definition is. Higher wins, same as numbering rules.</summary>
    public int Specificity => SiteId is null ? 0 : 1;

    /// <summary>
    /// Replaces the route wholesale. Steps are renumbered from 1 in the order supplied, so a
    /// caller can't produce a route with gaps or duplicate positions — a route where step 2 is
    /// missing would leave a document permanently stuck between steps 1 and 3.
    /// </summary>
    public void SetSteps(IEnumerable<(Guid RoleId, SignatureRole Role, string StepLabel)> steps)
    {
        if (IsActive)
        {
            // An active definition is what new submissions resolve against. Editing it in
            // place would change the route between a user reading the form and submitting it.
            throw new InvalidOperationException(
                $"Workflow '{Name}' is active; deactivate it before changing its steps.");
        }

        var ordered = steps.ToList();

        _steps.Clear();
        for (var index = 0; index < ordered.Count; index++)
        {
            var (roleId, role, label) = ordered[index];
            _steps.Add(new WorkflowStepDefinition(Id, index + 1, roleId, role, label));
        }

        Version++;
        Touch();
    }

    /// <summary>
    /// Makes this the route new submissions use.
    /// <para>
    /// Guarded rather than a simple flag flip. A route with no approver would let a document
    /// reach Approved on reviewer signatures alone, and an empty route would let it reach
    /// Approved on none — both are the kind of misconfiguration that produces an issued SOP
    /// nobody authorised.
    /// </para>
    /// </summary>
    public void Activate()
    {
        if (_steps.Count == 0)
        {
            throw new InvalidOperationException(
                $"Workflow '{Name}' has no steps and cannot be activated.");
        }

        if (_steps.All(step => step.Role != SignatureRole.Approver))
        {
            throw new InvalidOperationException(
                $"Workflow '{Name}' has no approver step; reviewers alone cannot authorise issue.");
        }

        IsActive = true;
        Touch();
    }

    public void Deactivate()
    {
        IsActive = false;
        Touch();
    }

    private void Touch() => UpdatedAt = DateTimeOffset.UtcNow;
}

/// <summary>
/// One position on a route definition: which role signs, in what order, and what the step is
/// called on the document face.
/// </summary>
public class WorkflowStepDefinition : Entity
{
    private WorkflowStepDefinition() { }

    public WorkflowStepDefinition(
        Guid workflowDefinitionId,
        int stepOrder,
        Guid roleId,
        SignatureRole role,
        string stepLabel)
    {
        WorkflowDefinitionId = workflowDefinitionId;
        StepOrder = stepOrder > 0
            ? stepOrder
            : throw new ArgumentOutOfRangeException(nameof(stepOrder), "Step order starts at 1.");
        RoleId = roleId;
        Role = role;
        StepLabel = string.IsNullOrWhiteSpace(stepLabel)
            ? throw new ArgumentException("Step label is required.", nameof(stepLabel))
            : stepLabel.Trim();
    }

    public Guid WorkflowDefinitionId { get; private set; }

    public int StepOrder { get; private set; }

    /// <summary>The role whose holders are eligible to sign this step.</summary>
    public Guid RoleId { get; private set; }

    public SignatureRole Role { get; private set; }

    /// <summary>"Reviewed By", "Approved By", "QA Head" — what prints against the signature.</summary>
    public string StepLabel { get; private set; } = "";
}
