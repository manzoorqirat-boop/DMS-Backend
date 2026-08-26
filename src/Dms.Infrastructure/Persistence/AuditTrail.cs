using Dms.Application.Abstractions;
using Dms.Application.Common;
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
        string? details = null,
        string? actor = null)
    {
        // Services already reject unattributable requests before reaching this point, so a
        // null here (with no explicit actor override either) means a code path skipped that
        // check. Throwing beats writing "unknown" into a regulated trail and calling it
        // attribution.
        var resolvedActor = actor ?? currentUser.UserName;
        if (string.IsNullOrWhiteSpace(resolvedActor))
        {
            throw new InvalidOperationException(
                $"Cannot record {action} for {entityType} {entityId}: no attributable actor.");
        }

        db.AuditEvents.Add(new AuditEvent(action, entityType, entityId, entityLabel, resolvedActor, details));
    }

    public async Task<PagedResult<AuditEvent>> ListAsync(
        Guid? entityId,
        string? entityType,
        string? actor,
        DateTimeOffset? from,
        DateTimeOffset? to,
        PagedRequest paging,
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

        if (!string.IsNullOrWhiteSpace(actor))
        {
            query = query.Where(x => x.Actor == actor);
        }

        // Date bounds matter more here than anywhere else: "show me everything that happened
        // to this document between these dates" is the question an inspector actually asks.
        if (from is { } start)
        {
            query = query.Where(x => x.OccurredAt >= start);
        }

        if (to is { } end)
        {
            query = query.Where(x => x.OccurredAt <= end);
        }

        return await query
            .OrderByDescending(x => x.OccurredAt)
            .ToPagedResultAsync(paging, cancellationToken);
    }
}
