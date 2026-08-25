using Dms.Application.Common;
using Dms.Domain.Entities;
using Dms.Domain.Enums;

namespace Dms.Application.Abstractions;

/// <summary>
/// Records audit events.
/// <para>
/// <see cref="Record"/> enqueues rather than writes: the event is flushed by whichever
/// <c>SaveChanges</c> the calling service was already going to perform, which means the audit
/// entry and the change it describes commit together or not at all. An audit trail written in
/// a separate transaction can record an approval that then failed to persist, or miss one that
/// succeeded — both are worse than no trail, because they're wrong rather than absent.
/// </para>
/// </summary>
public interface IAuditTrail
{
    /// <param name="entityLabel">
    /// Human-readable identity of the subject at the time of the event — document number,
    /// template name and version, site code.
    /// </param>
    void Record(
        AuditAction action,
        string entityType,
        Guid entityId,
        string entityLabel,
        string? details = null);
}

public interface IAuditQuery
{
    Task<PagedResult<AuditEvent>> ListAsync(
        Guid? entityId,
        string? entityType,
        string? actor,
        DateTimeOffset? from,
        DateTimeOffset? to,
        PagedRequest paging,
        CancellationToken cancellationToken);
}
