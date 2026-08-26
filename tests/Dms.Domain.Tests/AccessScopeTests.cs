using Dms.Domain.Entities;
using Dms.Domain.Enums;
using Xunit;
using Dms.Domain.Common;

namespace Dms.Domain.Tests;

/// <summary>
/// Scope precedence on role assignments. Getting this wrong grants someone authority at a site
/// they have nothing to do with, which a global-only role model can't even express.
/// </summary>
public class AccessScopeTests
{
    private static readonly Guid SiteA = Uuid7.NewGuid();
    private static readonly Guid SiteB = Uuid7.NewGuid();
    private static readonly Guid QaAtSiteA = Uuid7.NewGuid();
    private static readonly Guid ProductionAtSiteA = Uuid7.NewGuid();

    private static UserRoleAssignment Assignment(Guid? siteId, Guid? departmentId) =>
        new(Uuid7.NewGuid(), Uuid7.NewGuid(), siteId, departmentId, "admin");

    [Fact]
    public void Global_assignment_applies_everywhere()
    {
        var assignment = Assignment(null, null);

        Assert.Equal(AssignmentScope.Global, assignment.Scope);
        Assert.True(assignment.AppliesTo(SiteA, QaAtSiteA));
        Assert.True(assignment.AppliesTo(SiteB, null));
        Assert.True(assignment.AppliesTo(null, null));
    }

    [Fact]
    public void Site_assignment_covers_every_department_at_that_site_only()
    {
        var assignment = Assignment(SiteA, null);

        Assert.Equal(AssignmentScope.Site, assignment.Scope);
        Assert.True(assignment.AppliesTo(SiteA, QaAtSiteA));
        Assert.True(assignment.AppliesTo(SiteA, ProductionAtSiteA));
        Assert.False(assignment.AppliesTo(SiteB, null));
    }

    [Fact]
    public void Department_assignment_covers_only_that_department()
    {
        var assignment = Assignment(SiteA, QaAtSiteA);

        Assert.Equal(AssignmentScope.Department, assignment.Scope);
        Assert.True(assignment.AppliesTo(SiteA, QaAtSiteA));
        Assert.False(assignment.AppliesTo(SiteA, ProductionAtSiteA));
        Assert.False(assignment.AppliesTo(SiteB, QaAtSiteA));
    }

    [Fact]
    public void An_unscoped_question_is_only_satisfied_by_a_global_grant()
    {
        // Listing everything, say. Answering "yes" for a site-scoped grant would silently
        // widen it.
        Assert.False(Assignment(SiteA, null).AppliesTo(null, null));
        Assert.False(Assignment(SiteA, QaAtSiteA).AppliesTo(null, null));
        Assert.True(Assignment(null, null).AppliesTo(null, null));
    }

    [Fact]
    public void A_department_scoped_grant_must_name_its_site()
    {
        // Otherwise it is ambiguous the moment two sites have departments with the same code.
        Assert.Throws<ArgumentException>(() => Assignment(null, QaAtSiteA));
    }

    [Fact]
    public void Setting_permissions_replaces_rather_than_accumulates()
    {
        var role = new Role("QA_REVIEWER", "QA Reviewer");

        role.SetPermissions([Permission.DocumentView, Permission.DocumentSign, Permission.AuditView]);
        role.SetPermissions([Permission.DocumentView]);

        Assert.True(role.Grants(Permission.DocumentView));
        Assert.False(role.Grants(Permission.DocumentSign));
        Assert.False(role.Grants(Permission.AuditView));
    }

    [Fact]
    public void A_deactivated_role_grants_nothing()
    {
        var role = new Role("QA_REVIEWER", "QA Reviewer");
        role.SetPermissions([Permission.DocumentView]);
        role.Deactivate();

        Assert.False(role.Grants(Permission.DocumentView));
    }

    [Fact]
    public void A_system_role_cannot_be_renamed_or_deactivated()
    {
        // Otherwise a well-meaning admin can delete the only role holding RoleManage and lock
        // everyone out.
        var role = new Role("SYSTEM_ADMIN", "System Administrator", isSystem: true);

        Assert.Throws<InvalidOperationException>(() => role.Rename("Something Else", null));
        Assert.Throws<InvalidOperationException>(() => role.Deactivate());
    }

    [Fact]
    public void A_system_roles_grants_can_still_be_edited()
    {
        // A system role that couldn't be adjusted would be a hardcoded role wearing a costume.
        var role = new Role("SYSTEM_ADMIN", "System Administrator", isSystem: true);

        role.SetPermissions([Permission.RoleManage]);

        Assert.True(role.Grants(Permission.RoleManage));
    }
}
