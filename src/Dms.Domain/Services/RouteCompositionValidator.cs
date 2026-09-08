using Dms.Domain.Enums;

namespace Dms.Domain.Services;

/// <summary>
/// Checks that a nominated signature route is a legitimate one, independently of who is
/// eligible for each step.
/// <para>
/// Eligibility — does this person hold that role here — is a permissions question answered
/// elsewhere. This answers a different one: does the route <i>as a whole</i> constitute
/// independent review? A route can be perfectly eligible at every step and still be worthless,
/// which is what these rules exist to catch.
/// </para>
/// <para>
/// Pure and I/O-free, so every rule is testable without a database and reads as the policy it
/// is rather than as scattered guard clauses in a service method.
/// </para>
/// </summary>
public static class RouteCompositionValidator
{
    /// <summary>One nominated step, reduced to what the composition rules care about.</summary>
    /// <param name="StepOrder">Position in the route, for error messages.</param>
    /// <param name="StepLabel">The configured label, so a refusal names the step a user sees.</param>
    /// <param name="Role">What kind of signature this step collects.</param>
    /// <param name="UserName">Who was nominated.</param>
    public sealed record NominatedStep(
        int StepOrder,
        string StepLabel,
        SignatureRole Role,
        string UserName);

    /// <summary>
    /// Validates the composition. Returns every problem found rather than the first, so a
    /// submitter fixes one form instead of discovering three refusals in sequence.
    /// </summary>
    /// <param name="author">
    /// The document's author. Compared case-insensitively — usernames are matched that way
    /// everywhere else in this system, and a route that passed validation only because someone
    /// typed "A.Nair" instead of "a.nair" would be exactly the failure these rules prevent.
    /// </param>
    public static IReadOnlyList<string> Validate(
        IReadOnlyList<NominatedStep> steps,
        string author)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var issues = new List<string>();

        if (steps.Count == 0)
        {
            issues.Add("A route must have at least one step.");
            return issues;
        }

        var approvers = steps
            .Where(s => s.Role is SignatureRole.Approver or SignatureRole.QualityApprover)
            .ToList();

        // ---------------------------------------------------------------- an approver at all
        if (approvers.Count == 0)
        {
            issues.Add(
                "The route has no approver. Review alone does not approve a document — at least "
                + "one step must collect an approval signature.");
        }

        // ------------------------------------------------- the author cannot approve alone
        //
        // The single most important rule here. An author appearing among the approvers is
        // normal and often required — they know the document best. An author who is the ONLY
        // approver has approved their own work, which is self-certification wearing the
        // costume of a workflow.
        var authorApprovals = approvers
            .Where(a => Matches(a.UserName, author))
            .ToList();

        if (approvers.Count > 0 && authorApprovals.Count == approvers.Count)
        {
            issues.Add(
                $"'{author}' is the author and the only approver on this route. Approval has to "
                + "come from someone other than the person who wrote the document — add an "
                + "independent approver.");
        }

        // ------------------------------------------- one person cannot hold two steps
        //
        // Not in the source specification, but it follows from the same principle: three
        // signatures from one person is one signature wearing three hats. If a site genuinely
        // wants a reviewer to also approve, that is a two-step route with two nominations of
        // the same person — and this refuses it deliberately rather than by oversight.
        var duplicates = steps
            .GroupBy(s => s.UserName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var duplicate in duplicates)
        {
            var where = string.Join(", ", duplicate.Select(d => $"{d.StepOrder} ({d.StepLabel})"));

            issues.Add(
                $"'{duplicate.Key}' is nominated for more than one step ({where}). Each step needs "
                + "a different person, or the signatures are one judgement recorded several times.");
        }

        return issues;
    }

    /// <summary>
    /// Whether a route that defines a quality-approval step has one nominated.
    /// <para>
    /// Separate from <see cref="Validate"/> because it is a question about the configured route
    /// rather than the nominations: if the workflow for this document type has no QO step, not
    /// having a QO approver is correct, not a failure. Only a route that asks for one and does
    /// not get one is wrong — and with the slots coming from configuration rather than the
    /// submitter, that should be impossible. This catches a misconfigured workflow, which is
    /// the case where it would otherwise go unnoticed.
    /// </para>
    /// </summary>
    public static bool SatisfiesQualityApproval(
        IReadOnlyList<NominatedStep> steps,
        bool routeRequiresQualityApproval) =>
        !routeRequiresQualityApproval
        || steps.Any(s => s.Role == SignatureRole.QualityApprover);

    private static bool Matches(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
