using Dms.Domain.Enums;
using Dms.Domain.Services;
using Xunit;

namespace Dms.Domain.Tests;

public class RouteCompositionValidatorTests
{
    private static RouteCompositionValidator.NominatedStep Step(
        int order, SignatureRole role, string userName) =>
        new(order, $"Step {order}", role, userName);

    [Fact]
    public void A_route_with_a_reviewer_and_an_independent_approver_is_valid()
    {
        var issues = RouteCompositionValidator.Validate(
            [Step(1, SignatureRole.Reviewer, "r.khan"), Step(2, SignatureRole.Approver, "s.patel")],
            author: "a.nair");

        Assert.Empty(issues);
    }

    [Fact]
    public void The_author_may_be_one_of_several_approvers()
    {
        // Normal and often required — the author knows the document best. What matters is that
        // they are not the only one.
        var issues = RouteCompositionValidator.Validate(
            [Step(1, SignatureRole.Approver, "a.nair"), Step(2, SignatureRole.Approver, "s.patel")],
            author: "a.nair");

        Assert.Empty(issues);
    }

    [Fact]
    public void The_author_cannot_be_the_only_approver()
    {
        // Self-certification wearing the costume of a workflow.
        var issues = RouteCompositionValidator.Validate(
            [Step(1, SignatureRole.Reviewer, "r.khan"), Step(2, SignatureRole.Approver, "a.nair")],
            author: "a.nair");

        Assert.Contains(issues, i => i.Contains("only approver", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_author_check_ignores_username_casing()
    {
        // A route that passed only because someone typed a capital letter would be exactly the
        // failure this rule exists to prevent.
        var issues = RouteCompositionValidator.Validate(
            [Step(1, SignatureRole.Approver, "A.Nair")],
            author: "a.nair");

        Assert.Contains(issues, i => i.Contains("only approver", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_route_of_reviewers_only_is_refused()
    {
        // Review alone does not approve a document.
        var issues = RouteCompositionValidator.Validate(
            [Step(1, SignatureRole.Reviewer, "r.khan"), Step(2, SignatureRole.Reviewer, "s.patel")],
            author: "a.nair");

        Assert.Contains(issues, i => i.Contains("no approver", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_quality_approver_counts_as_an_approver()
    {
        var issues = RouteCompositionValidator.Validate(
            [Step(1, SignatureRole.Reviewer, "r.khan"),
             Step(2, SignatureRole.QualityApprover, "q.sharma")],
            author: "a.nair");

        Assert.Empty(issues);
    }

    [Fact]
    public void One_person_cannot_hold_two_steps()
    {
        // Three signatures from one person is one signature wearing three hats.
        var issues = RouteCompositionValidator.Validate(
            [Step(1, SignatureRole.Reviewer, "s.patel"), Step(2, SignatureRole.Approver, "s.patel")],
            author: "a.nair");

        Assert.Contains(issues, i => i.Contains("more than one step", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_duplicate_check_ignores_casing_too()
    {
        var issues = RouteCompositionValidator.Validate(
            [Step(1, SignatureRole.Reviewer, "s.patel"), Step(2, SignatureRole.Approver, "S.Patel")],
            author: "a.nair");

        Assert.Contains(issues, i => i.Contains("more than one step", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        // A submitter should fix one form, not discover three refusals in sequence.
        var issues = RouteCompositionValidator.Validate(
            [Step(1, SignatureRole.Reviewer, "a.nair"), Step(2, SignatureRole.Reviewer, "a.nair")],
            author: "a.nair");

        Assert.Equal(2, issues.Count);
    }

    [Fact]
    public void An_empty_route_is_refused()
    {
        Assert.Single(RouteCompositionValidator.Validate([], author: "a.nair"));
    }

    [Fact]
    public void Quality_approval_is_only_required_when_the_route_defines_it()
    {
        // A workflow with no QO step is correct without a QO approver. Only a route that asks
        // for one and does not get it is wrong.
        var withoutQo = new[] { Step(1, SignatureRole.Approver, "s.patel") };

        Assert.True(RouteCompositionValidator.SatisfiesQualityApproval(withoutQo, false));
        Assert.False(RouteCompositionValidator.SatisfiesQualityApproval(withoutQo, true));
    }

    [Fact]
    public void A_route_with_a_quality_approver_satisfies_the_requirement()
    {
        var withQo = new[]
        {
            Step(1, SignatureRole.Approver, "s.patel"),
            Step(2, SignatureRole.QualityApprover, "q.sharma"),
        };

        Assert.True(RouteCompositionValidator.SatisfiesQualityApproval(withQo, true));
    }
}
