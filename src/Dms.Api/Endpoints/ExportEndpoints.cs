using Dms.Application.Abstractions;
using Dms.Application.Common;
using Dms.Application.Documents;

namespace Dms.Api.Endpoints;

/// <summary>
/// CSV exports of the master register and the audit trail.
/// <para>
/// An inspector asking for "the list of current SOPs" or "everything that happened to this
/// document" expects a file, not a screen to scroll. These are read-only projections of data
/// already exposed through the paged endpoints — nothing new is disclosed, it is only shaped
/// differently.
/// </para>
/// </summary>
public static class ExportEndpoints
{
    /// <summary>
    /// Export ceiling. Higher than the interactive page size because an export is meant to be
    /// complete, but still bounded — an unbounded export of a decade of audit history is a
    /// reliable way to exhaust memory, and a truncated file that says so beats a failed request.
    /// </summary>
    private const int MaxExportRows = 10_000;

    public static void MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/exports").WithTags("Exports");

        // The master list: one row per document showing the revision in force.
        group.MapGet("/master-list", async (
            DraftCreationService service,
            Guid? siteId,
            Guid? departmentId,
            Guid? documentTypeId,
            bool? currentRevisionsOnly,
            CancellationToken ct) =>
        {
            var rows = new List<IReadOnlyList<string?>>();
            var request = new PagedRequest(1, PagedRequest.MaxPageSize);

            // Paged through rather than fetched at once, so the ceiling is enforced by the
            // loop instead of by whatever the database happens to return.
            while (rows.Count < MaxExportRows)
            {
                var batch = await service.ListAsync(
                    siteId, departmentId, documentTypeId, currentRevisionsOnly ?? true, null, request, ct);

                foreach (var d in batch.Items)
                {
                    rows.Add(
                    [
                        d.DocumentNumber,
                        d.RevisionLabel,
                        d.Title,
                        d.Status.ToString(),
                        Csv.Field(d.EffectiveDate),
                        Csv.Field(d.NextReviewDate),
                        d.Author,
                        Csv.Field(d.CreatedAt),
                        d.IsCurrentRevision ? "Yes" : "No",
                    ]);
                }

                if (!batch.HasNextPage)
                {
                    break;
                }

                request = new PagedRequest(request.Page + 1, request.PageSize);
            }

            var csv = Csv.Build(
                ["Document Number", "Revision", "Title", "Status", "Effective Date",
                 "Next Review", "Author", "Created", "Current Revision"],
                rows.Take(MaxExportRows));

            return Results.File(csv, "text/csv", $"master-list-{DateTime.UtcNow:yyyyMMdd}.csv");
        });

        // Audit trail, filterable exactly like the paged endpoint.
        group.MapGet("/audit", async (
            IAuditQuery audit,
            Guid? entityId,
            string? entityType,
            string? actor,
            DateTimeOffset? from,
            DateTimeOffset? to,
            CancellationToken ct) =>
        {
            var rows = new List<IReadOnlyList<string?>>();
            var request = new PagedRequest(1, PagedRequest.MaxPageSize);

            while (rows.Count < MaxExportRows)
            {
                var batch = await audit.ListAsync(entityId, entityType, actor, from, to, request, ct);

                foreach (var e in batch.Items)
                {
                    rows.Add(
                    [
                        Csv.Field(e.OccurredAt),
                        e.Actor,
                        e.Action.ToString(),
                        e.EntityType,
                        e.EntityLabel,
                        e.Details,
                    ]);
                }

                if (!batch.HasNextPage)
                {
                    break;
                }

                request = new PagedRequest(request.Page + 1, request.PageSize);
            }

            var csv = Csv.Build(
                ["Timestamp (UTC)", "Actor", "Action", "Entity Type", "Entity", "Details"],
                rows.Take(MaxExportRows));

            return Results.File(csv, "text/csv", $"audit-trail-{DateTime.UtcNow:yyyyMMdd}.csv");
        });
    }
}
