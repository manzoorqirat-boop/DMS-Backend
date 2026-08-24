using Dms.Application.Abstractions;
using Dms.Application.Documents;

namespace Dms.Api.Endpoints;

public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/audit").WithTags("Audit");

        // Read-only, and there is deliberately no POST, PUT, PATCH or DELETE here. The trail
        // is written as a side effect of the operations it describes; an endpoint that let a
        // caller author or amend an entry directly would defeat the point of having one.
        group.MapGet("/", async (
            IAuditQuery audit,
            Guid? entityId,
            string? entityType,
            int? limit,
            CancellationToken ct) =>
        {
            var events = await audit.ListAsync(entityId, entityType, limit ?? 100, ct);

            return Results.Ok(events.Select(e => new
            {
                e.Id,
                e.OccurredAt,
                e.Actor,
                Action = e.Action.ToString(),
                e.EntityType,
                e.EntityId,
                e.EntityLabel,
                e.Details,
            }));
        });
    }

    public static void MapIntegrityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/documents/{id:guid}/verify", async (
            DraftCreationService service,
            Guid id,
            CancellationToken ct) =>
            (await service.VerifyWorkingCopyAsync(id, ct)).ToHttpResult())
            .WithTags("Documents");
    }
}
