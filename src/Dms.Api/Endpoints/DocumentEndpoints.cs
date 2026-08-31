using Dms.Application.Common;
using Dms.Application.Documents;
using Dms.Domain.Enums;

namespace Dms.Api.Endpoints;

public static class DocumentEndpoints
{
    private const string DocxContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public static void MapOrganisationEndpoints(this IEndpointRouteBuilder app)
    {
        var sites = app.MapGroup("/api/sites").WithTags("Sites");

        sites.MapGet("/", async (
            OrganisationService service,
            bool? includeInactive,
            CancellationToken ct) =>
            Results.Ok(await service.ListSitesAsync(includeInactive ?? false, ct)));

        sites.MapPost("/", async (
            OrganisationService service,
            CreateSiteRequest request,
            CancellationToken ct) =>
        {
            var result = await service.CreateSiteAsync(request, ct);
            return result.ToHttpResult(created => Results.Created($"/api/sites/{created.Id}", created));
        });

        var departments = app.MapGroup("/api/departments").WithTags("Departments");

        departments.MapGet("/", async (
            OrganisationService service,
            Guid? siteId,
            bool? includeInactive,
            CancellationToken ct) =>
            Results.Ok(await service.ListDepartmentsAsync(siteId, includeInactive ?? false, ct)));

        departments.MapPost("/", async (
            OrganisationService service,
            CreateDepartmentRequest request,
            CancellationToken ct) =>
        {
            var result = await service.CreateDepartmentAsync(request, ct);
            return result.ToHttpResult(created => Results.Created($"/api/departments/{created.Id}", created));
        });
    }

    public static void MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/documents").WithTags("Documents");

        // The master register view: every controlled document, filterable by where it sits in
        // the site/department/type hierarchy.
        // Defaults to the master list — one row per document showing the revision in force.
        // Pass currentRevisionsOnly=false for the full register including superseded revisions.
        group.MapGet("/", async (
            DraftCreationService service,
            Guid? siteId,
            Guid? departmentId,
            Guid? documentTypeId,
            bool? currentRevisionsOnly,
            string? search,
            DocumentStatus? status,
            int? page,
            int? pageSize,
            CancellationToken ct) =>
            Results.Ok(await service.ListAsync(
                siteId, departmentId, documentTypeId, currentRevisionsOnly ?? true, search, status,
                new PagedRequest(page, pageSize), ct)));

        group.MapGet("/{id:guid}/revisions", async (
            DocumentRevisionService service,
            Guid id,
            CancellationToken ct) =>
            (await service.ListRevisionsAsync(id, ct)).ToHttpResult());

        // Opens Rev n+1 from the version currently in force, as a new Draft record. The
        // predecessor is untouched until the successor is actually issued.
        group.MapPost("/{id:guid}/revise", async (
            DocumentRevisionService service,
            Guid id,
            ReviseRequest request,
            CancellationToken ct) =>
        {
            var result = await service.BeginRevisionAsync(id, request.Reason, ct);
            return result.ToHttpResult(created =>
                Results.Created($"/api/documents/{created.Id}", created));
        });

        group.MapGet("/{id:guid}", async (
            DraftCreationService service,
            Guid id,
            CancellationToken ct) =>
            (await service.GetAsync(id, ct)).ToHttpResult());

        group.MapPost("/", async (
            DraftCreationService service,
            CreateDraftRequest request,
            CancellationToken ct) =>
        {
            var result = await service.CreateDraftAsync(request, ct);
            return result.ToHttpResult(created => Results.Created($"/api/documents/{created.Id}", created));
        });

        group.MapPost("/{id:guid}/withdraw", async (
            DraftCreationService service,
            Guid id,
            CancellationToken ct) =>
            (await service.WithdrawAsync(id, ct)).ToHttpResult());

        // Inspection only. This does not become the author's editing path — that's the
        // document server in Phase 3, precisely because URS Functions #13 forbids the real
        // file reaching a client PC.
        // The approved artefact, with its signature manifest. Generated on first request and
        // cached — see ApprovedPdfService for why this isn't produced at the moment of
        // approval.
        group.MapGet("/{id:guid}/approved-pdf", async (
            ApprovedPdfService service,
            Guid id,
            CancellationToken ct) =>
        {
            var result = await service.GetOrCreateAsync(id, ct);

            return result.ToHttpResult(file =>
                Results.File(file.Content, "application/pdf", file.FileName));
        });

        group.MapGet("/{id:guid}/working-copy", async (
            DraftCreationService service,
            Guid id,
            CancellationToken ct) =>
        {
            var result = await service.DownloadWorkingCopyAsync(id, ct);
            return result.ToHttpResult(file => Results.File(file.Content, DocxContentType, file.FileName));
        });
    }
}

public sealed record ReviseRequest(string Reason);
