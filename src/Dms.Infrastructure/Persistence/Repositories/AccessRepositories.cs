using Dms.Application.Abstractions;
using Dms.Application.Common;
using Dms.Domain.Entities;
using Dms.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Dms.Infrastructure.Persistence.Repositories;

/// <summary>
/// Evaluates the privilege matrix for the current user.
/// <para>
/// Deliberately queries live on each call rather than caching a permission set for the request.
/// Revoking a role has to take effect immediately — a cached grant that outlives its revocation
/// is precisely the finding an access review is looking for — and these are small indexed
/// lookups against tables that change rarely.
/// </para>
/// </summary>
public sealed class AccessControl(DmsDbContext db, ICurrentUser currentUser) : IAccessControl
{
    public async Task<bool> HasPermissionAsync(
        Permission permission,
        Guid? siteId,
        Guid? departmentId,
        CancellationToken cancellationToken)
    {
        var permissions = await EffectivePermissionsAsync(siteId, departmentId, cancellationToken);
        return permissions.Contains(permission);
    }

    public async Task<IReadOnlyList<Permission>> EffectivePermissionsAsync(
        Guid? siteId,
        Guid? departmentId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentUser.UserName))
        {
            return [];
        }

        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserName == currentUser.UserName, cancellationToken);

        // A deactivated account keeps its assignments but holds no privileges. Clearing the
        // grants on deactivation instead would destroy the record of what the person could do,
        // which is the thing an access review needs when investigating past activity.
        if (user is null || !user.IsActive)
        {
            return [];
        }

        var candidates = await db.UserRoleAssignments
            .AsNoTracking()
            .Where(a => a.UserId == user.Id)
            .Join(
                db.Roles.AsNoTracking().Where(r => r.IsActive),
                a => a.RoleId,
                r => r.Id,
                (a, r) => new { Assignment = a, RoleId = r.Id })
            .ToListAsync(cancellationToken);

        // Scope filtering happens in memory because AppliesTo encodes the precedence rules and
        // belongs in the entity, not duplicated as a SQL predicate that can drift from it. The
        // row count here is per-user and small.
        var applicableRoleIds = candidates
            .Where(x => x.Assignment.AppliesTo(siteId, departmentId))
            .Select(x => x.RoleId)
            .Distinct()
            .ToList();

        if (applicableRoleIds.Count == 0)
        {
            return [];
        }

        return await db.RolePermissions
            .AsNoTracking()
            .Where(rp => applicableRoleIds.Contains(rp.RoleId))
            .Select(rp => rp.Permission)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DmsUser>> UsersWithPermissionAsync(
        Permission permission,
        Guid? siteId,
        Guid? departmentId,
        CancellationToken cancellationToken)
    {
        var roleIds = await db.RolePermissions
            .AsNoTracking()
            .Where(rp => rp.Permission == permission)
            .Select(rp => rp.RoleId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (roleIds.Count == 0)
        {
            return [];
        }

        var assignments = await db.UserRoleAssignments
            .AsNoTracking()
            .Where(a => roleIds.Contains(a.RoleId))
            .ToListAsync(cancellationToken);

        var userIds = assignments
            .Where(a => a.AppliesTo(siteId, departmentId))
            .Select(a => a.UserId)
            .Distinct()
            .ToList();

        return await db.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id) && u.IsActive)
            .OrderBy(u => u.UserName)
            .ToListAsync(cancellationToken);
    }
}

public sealed class RoleRepository(DmsDbContext db) : IRoleRepository
{
    public Task<Role?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<Role?> GetByCodeAsync(string code, CancellationToken cancellationToken) =>
        db.Roles.Include(r => r.Permissions)
            .FirstOrDefaultAsync(x => x.Code == code.ToUpperInvariant(), cancellationToken);

    public async Task<IReadOnlyList<Role>> ListAsync(bool includeInactive, CancellationToken cancellationToken)
    {
        var query = db.Roles.Include(r => r.Permissions).AsQueryable();

        if (!includeInactive)
        {
            query = query.Where(x => x.IsActive);
        }

        return await query.OrderBy(x => x.Code).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UserRoleAssignment>> ListAssignmentsAsync(
        Guid? userId,
        Guid? roleId,
        CancellationToken cancellationToken)
    {
        var query = db.UserRoleAssignments.AsQueryable();

        if (userId is { } user)
        {
            query = query.Where(x => x.UserId == user);
        }

        if (roleId is { } role)
        {
            query = query.Where(x => x.RoleId == role);
        }

        return await query.OrderBy(x => x.CreatedAt).ToListAsync(cancellationToken);
    }

    public Task<UserRoleAssignment?> GetAssignmentAsync(Guid id, CancellationToken cancellationToken) =>
        db.UserRoleAssignments.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public void Add(Role role) => db.Roles.Add(role);

    public void AddAssignment(UserRoleAssignment assignment) => db.UserRoleAssignments.Add(assignment);

    public void RemoveAssignment(UserRoleAssignment assignment) => db.UserRoleAssignments.Remove(assignment);

    public Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken) =>
        SaveChangesTranslator.SaveAsync(db, cancellationToken);
}

public sealed class NumberingRuleRepository(DmsDbContext db) : INumberingRuleRepository
{
    public async Task<IReadOnlyList<NumberingRule>> FindCandidatesAsync(
        Guid documentTypeId,
        Guid siteId,
        CancellationToken cancellationToken) =>
        await db.NumberingRules
            .AsNoTracking()
            .Where(r => r.DocumentTypeId == documentTypeId && (r.SiteId == null || r.SiteId == siteId))
            .ToListAsync(cancellationToken);

    public Task<NumberingRule?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.NumberingRules.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<IReadOnlyList<NumberingRule>> ListAsync(
        Guid? documentTypeId,
        CancellationToken cancellationToken)
    {
        var query = db.NumberingRules.AsQueryable();

        if (documentTypeId is { } type)
        {
            query = query.Where(x => x.DocumentTypeId == type);
        }

        return await query.OrderBy(x => x.DocumentTypeId).ThenBy(x => x.SiteId).ToListAsync(cancellationToken);
    }

    public void Add(NumberingRule rule) => db.NumberingRules.Add(rule);

    public Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken) =>
        SaveChangesTranslator.SaveAsync(db, cancellationToken);
}

public sealed class WorkflowDefinitionRepository(DmsDbContext db) : IWorkflowDefinitionRepository
{
    public async Task<IReadOnlyList<WorkflowDefinition>> FindActiveCandidatesAsync(
        Guid documentTypeId,
        Guid siteId,
        CancellationToken cancellationToken) =>
        await db.WorkflowDefinitions
            .Include(d => d.Steps)
            .AsNoTracking()
            .Where(d => d.IsActive
                && d.DocumentTypeId == documentTypeId
                && (d.SiteId == null || d.SiteId == siteId))
            .ToListAsync(cancellationToken);

    public Task<WorkflowDefinition?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.WorkflowDefinitions.Include(d => d.Steps).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<IReadOnlyList<WorkflowDefinition>> ListAsync(
        Guid? documentTypeId,
        CancellationToken cancellationToken)
    {
        var query = db.WorkflowDefinitions.Include(d => d.Steps).AsQueryable();

        if (documentTypeId is { } type)
        {
            query = query.Where(x => x.DocumentTypeId == type);
        }

        return await query.OrderBy(x => x.DocumentTypeId).ThenBy(x => x.Name).ToListAsync(cancellationToken);
    }

    public void Add(WorkflowDefinition definition) => db.WorkflowDefinitions.Add(definition);

    public Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken) =>
        SaveChangesTranslator.SaveAsync(db, cancellationToken);
}

public sealed class MetadataFieldRepository(DmsDbContext db) : IMetadataFieldRepository
{
    public async Task<IReadOnlyList<MetadataFieldDefinition>> ListForTypeAsync(
        Guid documentTypeId,
        CancellationToken cancellationToken) =>
        await db.MetadataFieldDefinitions
            .Where(x => x.DocumentTypeId == documentTypeId)
            .OrderBy(x => x.DisplayOrder)
            .ToListAsync(cancellationToken);

    public Task<MetadataFieldDefinition?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.MetadataFieldDefinitions.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public void Add(MetadataFieldDefinition field) => db.MetadataFieldDefinitions.Add(field);

    public void Remove(MetadataFieldDefinition field) => db.MetadataFieldDefinitions.Remove(field);

    public Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken) =>
        SaveChangesTranslator.SaveAsync(db, cancellationToken);
}

public sealed class ReviewPolicyRepository(DmsDbContext db) : IReviewPolicyRepository
{
    public async Task<IReadOnlyList<ReviewPolicy>> FindCandidatesAsync(
        Guid documentTypeId,
        Guid siteId,
        CancellationToken cancellationToken) =>
        await db.ReviewPolicies
            .AsNoTracking()
            .Where(p => p.DocumentTypeId == documentTypeId && (p.SiteId == null || p.SiteId == siteId))
            .ToListAsync(cancellationToken);

    public Task<ReviewPolicy?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.ReviewPolicies.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<IReadOnlyList<ReviewPolicy>> ListAsync(
        Guid? documentTypeId,
        CancellationToken cancellationToken)
    {
        var query = db.ReviewPolicies.AsQueryable();

        if (documentTypeId is { } type)
        {
            query = query.Where(x => x.DocumentTypeId == type);
        }

        return await query.OrderBy(x => x.DocumentTypeId).ThenBy(x => x.SiteId).ToListAsync(cancellationToken);
    }

    public void Add(ReviewPolicy policy) => db.ReviewPolicies.Add(policy);

    public Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken) =>
        SaveChangesTranslator.SaveAsync(db, cancellationToken);
}
