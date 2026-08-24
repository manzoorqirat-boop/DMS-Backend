using Dms.Application.Abstractions;
using Dms.Domain.Entities;
using Dms.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Dms.Infrastructure.Persistence;

/// <summary>
/// Writes audit events into the same DbContext — and therefore the same transaction — as the
/// change they describe.
/// </summary>
public sealed class AuditTrail(DmsDbContext db, ICurrentUser currentUser) : IAuditTrail, IAuditQuery
{
    public void Record(
        AuditAction action,
        string entityType,
        Guid entityId,
        string entityLabel,
        string? details = null)
    {
        // Services already reject unattributable requests before reaching this point, so a
        // null here means a code path skipped that check. Throwing beats writing "unknown"
        // into a regulated trail and calling it attribution.
        var actor = currentUser.UserName;
        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new InvalidOperationException(
                $"Cannot record {action} for {entityType} {entityId}: no attributable actor.");
        }

        db.AuditEvents.Add(new AuditEvent(action, entityType, entityId, entityLabel, actor, details));
    }

    public async Task<IReadOnlyList<AuditEvent>> ListAsync(
        Guid? entityId,
        string? entityType,
        int limit,
        CancellationToken cancellationToken)
    {
        var query = db.AuditEvents.AsNoTracking();

        if (entityId is { } id)
        {
            query = query.Where(x => x.EntityId == id);
        }

        if (!string.IsNullOrWhiteSpace(entityType))
        {
            query = query.Where(x => x.EntityType == entityType);
        }

        return await query
            .OrderByDescending(x => x.OccurredAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);
    }
}
