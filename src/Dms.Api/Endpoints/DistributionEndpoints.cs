using Dms.Application.Common;
using Dms.Application.Distribution;

namespace Dms.Api.Endpoints;

public static class DistributionEndpoints
{
    public static void MapDistributionEndpoints(this IEndpointRouteBuilder app)
    {
        var documents = app.MapGroup("/api/documents").WithTags("Distribution");

        documents.MapGet("/{id:guid}/copies", async (
            DistributionService service,
            Guid id,
            CancellationToken ct) =>
            (await service.ListForDocumentAsync(id, ct)).ToHttpResult());

        // Only an Effective document can be distributed — putting a draft into someone's hands
        // is the distribution failure that actually causes harm on a shop floor.
        documents.MapPost("/{id:guid}/copies", async (
            DistributionService service,
            Guid id,
            IssueCopyRequest request,
            CancellationToken ct) =>
        {
            var result = await service.IssueAsync(id, request, ct);
            return result.ToHttpResult(created => Results.Created($"/api/copies/{created.Id}", created));
        });

        documents.MapGet("/{id:guid}/print-history", async (
            DistributionService service,
            Guid id,
            int? page,
            int? pageSize,
            CancellationToken ct) =>
            (await service.ListPrintHistoryAsync(id, new PagedRequest(page, pageSize), ct)).ToHttpResult());

        var copies = app.MapGroup("/api/copies").WithTags("Distribution");

        copies.MapPost("/{id:guid}/acknowledge", async (
            DistributionService service,
            Guid id,
            CancellationToken ct) =>
            (await service.AcknowledgeAsync(id, ct)).ToHttpResult());

        copies.MapPost("/{id:guid}/retrieve", async (
            DistributionService service,
            Guid id,
            CancellationToken ct) =>
            (await service.RetrieveAsync(id, ct)).ToHttpResult());

        copies.MapPost("/{id:guid}/close-out", async (
            DistributionService service,
            Guid id,
            CloseOutRequest request,
            CancellationToken ct) =>
            (await service.CloseOutAsync(id, request, ct)).ToHttpResult());

        // Enforces the print limit, records the event, and returns the rendered copy. The
        // watermark and scan code come back as headers so a caller can display them even when
        // the body is a file download.
        copies.MapPost("/{id:guid}/print", async (
            DistributionService service,
            Guid id,
            HttpResponse response,
            CancellationToken ct) =>
        {
            var result = await service.PrintAsync(id, ct);

            return result.ToHttpResult(print =>
            {
                response.Headers["X-Copy-Watermark"] = print.Watermark;
                response.Headers["X-Copy-Scan-Code"] = print.ScanCode;
                response.Headers["X-Copy-Print-Sequence"] = print.PrintSequence.ToString();

                // Announced explicitly. A caller must never mistake an unstamped file for a
                // controlled copy just because the request succeeded.
                response.Headers["X-Copy-Watermarked"] = print.IsWatermarked ? "true" : "false";

                return Results.File(
                    print.Content, print.ContentType, $"{print.ScanCode.Replace('/', '_')}.docx");
            });
        });

        var reports = app.MapGroup("/api/reports").WithTags("Reports");

        // The retrieval worklist: copies still out for documents no longer current. This is
        // what someone works through after a supersession.
        reports.MapGet("/pending-retrieval", async (
            DistributionService service,
            Guid? siteId,
            int? page,
            int? pageSize,
            CancellationToken ct) =>
            Results.Ok(await service.ListPendingRetrievalAsync(siteId, new PagedRequest(page, pageSize), ct)));
    }
}
