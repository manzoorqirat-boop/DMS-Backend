using Dms.Domain.Common;
using Dms.Domain.Enums;

namespace Dms.Domain.Entities;

/// <summary>
/// A named bundle of privileges — "QA Reviewer", "Plant Head", "Document Controller".
/// <para>
/// Roles are master data: an administrator composes them at runtime rather than a developer
/// declaring them, which is the whole point of the privilege matrix being a matrix. What can
/// be granted is fixed (see <see cref="Permission"/>); which combinations exist, and who holds
/// them, is not.
/// </para>
/// </summary>
public class Role : Entity, ITimestamped
{
    private readonly List<RolePermission> _permissions = [];

    private Role() { }

    public Role(string code, string name, string? description = null, bool isSystem = false)
    {
        Code = string.IsNullOrWhiteSpace(code)
            ? throw new ArgumentException("Role code is required.", nameof(code))
            : code.Trim().ToUpperInvariant();
        Name = RequireName(name);
        Description = description?.Trim();
        IsSystem = isSystem;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public string Code { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string? Description { get; private set; }

    /// <summary>
    /// A role the system depends on and an administrator may not delete or rename.
    /// <para>
    /// Exists so that a well-meaning admin can't lock everyone out by deleting the only role
    /// holding <see cref="Permission.RoleManage"/>. Its grants can still be edited — a system
    /// role that couldn't be adjusted would be a hardcoded role wearing a costume.
    /// </para>
    /// </summary>
    public bool IsSystem { get; private set; }

    public bool IsActive { get; private set; } = true;

    public IReadOnlyCollection<RolePermission> Permissions => _permissions;

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    public void Rename(string name, string? description)
    {
        if (IsSystem)
        {
            throw new InvalidOperationException($"Role '{Code}' is a system role and cannot be renamed.");
        }

        Name = RequireName(name);
        Description = description?.Trim();
        Touch();
    }

    /// <summary>
    /// Replaces the role's grants wholesale rather than adding one at a time.
    /// <para>
    /// Wholesale because the audit entry for a privilege change should describe the resulting
    /// matrix, not a stream of individual grants an auditor has to replay in order to work out
    /// what someone could do on a given date.
    /// </para>
    /// </summary>
    public void SetPermissions(IEnumerable<Permission> permissions)
    {
        var target = permissions.Distinct().ToList();

        _permissions.RemoveAll(existing => !target.Contains(existing.Permission));

        foreach (var permission in target.Where(p => _permissions.All(x => x.Permission != p)))
        {
            _permissions.Add(new RolePermission(Id, permission));
        }

        Touch();
    }

    public bool Grants(Permission permission) =>
        IsActive && _permissions.Any(x => x.Permission == permission);

    public void Deactivate()
    {
        if (IsSystem)
        {
            throw new InvalidOperationException($"Role '{Code}' is a system role and cannot be deactivated.");
        }

        IsActive = false;
        Touch();
    }

    public void Reactivate()
    {
        IsActive = true;
        Touch();
    }

    private void Touch() => UpdatedAt = DateTimeOffset.UtcNow;

    private static string RequireName(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Role name is required.", nameof(name))
            : name.Trim();
}

/// <summary>One cell of the privilege matrix: this role grants this permission.</summary>
public class RolePermission : Entity
{
    private RolePermission() { }

    public RolePermission(Guid roleId, Permission permission)
    {
        RoleId = roleId;
        Permission = permission;
    }

    public Guid RoleId { get; private set; }
    public Permission Permission { get; private set; }
}

/// <summary>
/// Grants a user a role, optionally narrowed to a site or a single department.
/// <para>
/// The scope is what stops "QA Reviewer" meaning "QA Reviewer everywhere". A user may hold
/// the same role at several scopes; the effective privilege at any point is the union of
/// whatever applies there.
/// </para>
/// </summary>
public class UserRoleAssignment : Entity, ITimestamped
{
    private UserRoleAssignment() { }

    public UserRoleAssignment(Guid userId, Guid roleId, Guid? siteId, Guid? departmentId, string assignedBy)
    {
        if (departmentId is not null && siteId is null)
        {
            // A department-scoped grant without its site is ambiguous the moment two sites
            // have departments with the same code, and unresolvable when checking scope.
            throw new ArgumentException("A department-scoped assignment must also name its site.", nameof(siteId));
        }

        UserId = userId;
        RoleId = roleId;
        SiteId = siteId;
        DepartmentId = departmentId;
        AssignedBy = string.IsNullOrWhiteSpace(assignedBy)
            ? throw new ArgumentException("Assignments must be attributable.", nameof(assignedBy))
            : assignedBy;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid UserId { get; private set; }
    public Guid RoleId { get; private set; }

    /// <summary>Null means organisation-wide.</summary>
    public Guid? SiteId { get; private set; }

    /// <summary>Null means every department within <see cref="SiteId"/>, or everywhere if that is null too.</summary>
    public Guid? DepartmentId { get; private set; }

    /// <summary>Who granted this. Kept on the row itself as well as in the audit trail.</summary>
    public string AssignedBy { get; private set; } = "";

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    public AssignmentScope Scope => (SiteId, DepartmentId) switch
    {
        (null, _) => AssignmentScope.Global,
        (not null, null) => AssignmentScope.Site,
        _ => AssignmentScope.Department,
    };

    /// <summary>
    /// Whether this assignment reaches a given site and department.
    /// <para>
    /// A request with no scope — listing all documents, say — is only satisfied by a Global
    /// assignment. Answering "yes" for a site-scoped grant would silently widen it, so an
    /// unscoped question gets the strict answer.
    /// </para>
    /// </summary>
    public bool AppliesTo(Guid? siteId, Guid? departmentId) => Scope switch
    {
        AssignmentScope.Global => true,
        AssignmentScope.Site => siteId == SiteId,
        _ => siteId == SiteId && departmentId == DepartmentId,
    };
}
