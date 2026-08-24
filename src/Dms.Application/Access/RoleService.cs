using Dms.Application.Abstractions;
using Dms.Application.Common;
using Dms.Domain.Entities;
using Dms.Domain.Enums;

namespace Dms.Application.Access;

/// <summary>
/// Administers the privilege matrix: roles, their grants, and who holds them where.
/// <para>
/// Every operation here requires <see cref="Permission.RoleManage"/> at global scope. Role
/// administration is deliberately not site-scopable — a site administrator who could grant
/// roles at their own site could grant themselves <c>RoleManage</c> and then grant themselves
/// anything anywhere, so scoping this particular permission would be scoping that does nothing.
/// </para>
/// </summary>
public sealed class RoleService(
    IRoleRepository roles,
    IUserRepository users,
    IAccessControl access,
    IAuditTrail audit,
    ICurrentUser currentUser)
{
    private const string RoleEntityType = "Role";
    private const string AssignmentEntityType = "UserRoleAssignment";

    public async Task<Result<RoleView>> CreateAsync(
        CreateRoleRequest request,
        CancellationToken cancellationToken)
    {
        var gate = await RequireRoleAdminAsync(cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        Role role;
        try
        {
            role = new Role(request.Code, request.Name, request.Description);
            role.SetPermissions(request.Permissions);
        }
        catch (ArgumentException ex)
        {
            return Error.Validation("role_invalid", ex.Message);
        }

        roles.Add(role);
        audit.Record(
            AuditAction.RoleCreated, RoleEntityType, role.Id, role.Code,
            DescribeGrants(request.Permissions));

        var outcome = await roles.SaveChangesAsync(cancellationToken);
        if (!outcome.Saved)
        {
            return outcome.ViolatedIndexContains("code")
                ? Error.Conflict("role_code_taken", $"A role with code '{role.Code}' already exists.")
                : Error.Conflict("role_save_conflict", "The role could not be saved.");
        }

        return RoleView.From(role);
    }

    /// <summary>
    /// Replaces a role's grants. The audit entry records the resulting matrix rather than the
    /// delta, so "what could this role do on that date" is answerable from one entry.
    /// </summary>
    public async Task<Result<RoleView>> SetPermissionsAsync(
        Guid roleId,
        IReadOnlyList<Permission> permissions,
        CancellationToken cancellationToken)
    {
        var gate = await RequireRoleAdminAsync(cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var role = await roles.GetAsync(roleId, cancellationToken);
        if (role is null)
        {
            return Error.NotFound("role_not_found", $"No role with id {roleId}.");
        }

        role.SetPermissions(permissions);
        audit.Record(
            AuditAction.RolePermissionsChanged, RoleEntityType, role.Id, role.Code,
            DescribeGrants(permissions));

        var outcome = await roles.SaveChangesAsync(cancellationToken);
        return outcome.Saved
            ? RoleView.From(role)
            : Error.Conflict("role_save_conflict", "The role could not be updated.");
    }

    public async Task<Result<IReadOnlyList<RoleView>>> ListAsync(
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var gate = await RequireRoleAdminAsync(cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var found = await roles.ListAsync(includeInactive, cancellationToken);
        return Result<IReadOnlyList<RoleView>>.Success(found.Select(RoleView.From).ToList());
    }

    /// <summary>Grants a user a role, optionally narrowed to a site or one department.</summary>
    public async Task<Result<AssignmentView>> AssignAsync(
        AssignRoleRequest request,
        CancellationToken cancellationToken)
    {
        var gate = await RequireRoleAdminAsync(cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var role = await roles.GetAsync(request.RoleId, cancellationToken);
        if (role is null)
        {
            return Error.NotFound("role_not_found", $"No role with id {request.RoleId}.");
        }

        if (!role.IsActive)
        {
            return Error.Validation("role_inactive", $"Role '{role.Code}' is deactivated and cannot be assigned.");
        }

        var user = await users.GetAsync(request.UserId, cancellationToken);
        if (user is null)
        {
            return Error.NotFound("user_not_found", $"No user with id {request.UserId}.");
        }

        UserRoleAssignment assignment;
        try
        {
            assignment = new UserRoleAssignment(
                user.Id, role.Id, request.SiteId, request.DepartmentId, currentUser.UserName!);
        }
        catch (ArgumentException ex)
        {
            return Error.Validation("assignment_invalid", ex.Message);
        }

        roles.AddAssignment(assignment);
        audit.Record(
            AuditAction.RoleAssigned, AssignmentEntityType, assignment.Id,
            $"{user.UserName} → {role.Code}",
            $"Scope {assignment.Scope}.");

        var outcome = await roles.SaveChangesAsync(cancellationToken);
        if (!outcome.Saved)
        {
            return outcome.ViolatedIndexContains("assignment")
                ? Error.Conflict("assignment_duplicate", "That user already holds that role at that scope.")
                : Error.Conflict("assignment_save_conflict", "The assignment could not be saved.");
        }

        return AssignmentView.From(assignment, user.UserName, role.Code);
    }

    /// <summary>
    /// Revokes an assignment. The row is deleted rather than flagged — but the audit entry
    /// recording who revoked what, and when, is not, so the history of who could do what
    /// survives the row it describes.
    /// </summary>
    public async Task<Result<bool>> RevokeAsync(Guid assignmentId, CancellationToken cancellationToken)
    {
        var gate = await RequireRoleAdminAsync(cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var assignment = await roles.GetAssignmentAsync(assignmentId, cancellationToken);
        if (assignment is null)
        {
            return Error.NotFound("assignment_not_found", $"No assignment with id {assignmentId}.");
        }

        var role = await roles.GetAsync(assignment.RoleId, cancellationToken);
        var user = await users.GetAsync(assignment.UserId, cancellationToken);

        roles.RemoveAssignment(assignment);
        audit.Record(
            AuditAction.RoleRevoked, AssignmentEntityType, assignment.Id,
            $"{user?.UserName ?? assignment.UserId.ToString()} → {role?.Code ?? assignment.RoleId.ToString()}",
            $"Scope {assignment.Scope}.");

        var outcome = await roles.SaveChangesAsync(cancellationToken);
        return outcome.Saved
            ? Result<bool>.Success(true)
            : Error.Conflict("assignment_save_conflict", "The assignment could not be revoked.");
    }

    public async Task<Result<IReadOnlyList<Permission>>> MyPermissionsAsync(
        Guid? siteId,
        Guid? departmentId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentUser.UserName))
        {
            return Error.Validation("actor_unknown", "The acting user could not be determined.");
        }

        var permissions = await access.EffectivePermissionsAsync(siteId, departmentId, cancellationToken);
        return Result<IReadOnlyList<Permission>>.Success(permissions);
    }

    /// <summary>
    /// Returns the reason the caller may not administer roles, or null when they may.
    /// <para>
    /// An <see cref="Error"/> rather than a <see cref="Result{T}"/> so the one guard serves
    /// callers returning different result types — <c>Result&lt;T&gt;</c> has an implicit
    /// conversion from Error, so each caller stays a three-line check.
    /// </para>
    /// </summary>
    private async Task<Error?> RequireRoleAdminAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentUser.UserName))
        {
            return Error.Validation("actor_unknown", "The acting user could not be determined.");
        }

        var allowed = await access.HasPermissionAsync(
            Permission.RoleManage, siteId: null, departmentId: null, cancellationToken);

        return allowed
            ? null
            : Error.Validation(
                "permission_denied",
                $"{Permission.RoleManage} at organisation-wide scope is required to administer roles.");
    }

    private static string DescribeGrants(IReadOnlyList<Permission> permissions) =>
        permissions.Count == 0
            ? "No permissions granted."
            : $"Grants: {string.Join(", ", permissions.Select(p => p.ToString()).OrderBy(x => x, StringComparer.Ordinal))}.";
}
