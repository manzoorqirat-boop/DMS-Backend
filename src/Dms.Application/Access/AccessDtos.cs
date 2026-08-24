using Dms.Domain.Entities;
using Dms.Domain.Enums;

namespace Dms.Application.Access;

public sealed record CreateRoleRequest(
    string Code,
    string Name,
    string? Description,
    IReadOnlyList<Permission> Permissions);

public sealed record RoleView(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    bool IsSystem,
    bool IsActive,
    IReadOnlyList<Permission> Permissions)
{
    public static RoleView From(Role role) => new(
        role.Id,
        role.Code,
        role.Name,
        role.Description,
        role.IsSystem,
        role.IsActive,
        role.Permissions.Select(x => x.Permission).OrderBy(x => x.ToString(), StringComparer.Ordinal).ToList());
}

public sealed record AssignRoleRequest(Guid UserId, Guid RoleId, Guid? SiteId, Guid? DepartmentId);

public sealed record AssignmentView(
    Guid Id,
    Guid UserId,
    string UserName,
    Guid RoleId,
    string RoleCode,
    Guid? SiteId,
    Guid? DepartmentId,
    AssignmentScope Scope,
    string AssignedBy,
    DateTimeOffset CreatedAt)
{
    public static AssignmentView From(UserRoleAssignment assignment, string userName, string roleCode) => new(
        assignment.Id,
        assignment.UserId,
        userName,
        assignment.RoleId,
        roleCode,
        assignment.SiteId,
        assignment.DepartmentId,
        assignment.Scope,
        assignment.AssignedBy,
        assignment.CreatedAt);
}
