using Dms.Application.Common;
using Dms.Domain.Entities;
using Dms.Domain.Enums;

namespace Dms.Application.Abstractions;

/// <summary>
/// Answers "may the current user do this, here?".
/// <para>
/// Scope is part of the question, not an afterthought. Asking only "may this user create
/// documents" would let a QA Reviewer at Site A create documents at Site B — which is the
/// failure a role model without scope always eventually produces.
/// </para>
/// </summary>
public interface IAccessControl
{
    /// <param name="siteId">Null asks the unscoped question, which only a Global grant satisfies.</param>
    Task<bool> HasPermissionAsync(
        Permission permission,
        Guid? siteId,
        Guid? departmentId,
        CancellationToken cancellationToken);

    /// <summary>Every permission the current user holds at a given scope. For building UI menus.</summary>
    Task<IReadOnlyList<Permission>> EffectivePermissionsAsync(
        Guid? siteId,
        Guid? departmentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Users who hold a permission at a scope. Used when building a signature route: the
    /// candidates for a "QA Head approves" step are the people who actually hold that role
    /// there, not an arbitrary user list.
    /// </summary>
    Task<IReadOnlyList<DmsUser>> UsersWithPermissionAsync(
        Permission permission,
        Guid? siteId,
        Guid? departmentId,
        CancellationToken cancellationToken);
}

public interface IRoleRepository
{
    Task<Role?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Role?> GetByCodeAsync(string code, CancellationToken cancellationToken);

    Task<IReadOnlyList<Role>> ListAsync(bool includeInactive, CancellationToken cancellationToken);

    Task<IReadOnlyList<UserRoleAssignment>> ListAssignmentsAsync(
        Guid? userId,
        Guid? roleId,
        CancellationToken cancellationToken);

    Task<UserRoleAssignment?> GetAssignmentAsync(Guid id, CancellationToken cancellationToken);

    void Add(Role role);

    void AddAssignment(UserRoleAssignment assignment);

    void RemoveAssignment(UserRoleAssignment assignment);

    Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken);
}

public interface INumberingRuleRepository
{
    /// <summary>
    /// Every rule that could apply to a type at a site. The caller picks the most specific —
    /// resolution order is a domain decision, not a query detail.
    /// </summary>
    Task<IReadOnlyList<NumberingRule>> FindCandidatesAsync(
        Guid documentTypeId,
        Guid siteId,
        CancellationToken cancellationToken);

    Task<NumberingRule?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<NumberingRule>> ListAsync(Guid? documentTypeId, CancellationToken cancellationToken);

    void Add(NumberingRule rule);

    Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken);
}
