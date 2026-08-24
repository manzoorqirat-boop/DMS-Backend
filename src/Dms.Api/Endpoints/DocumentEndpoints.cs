using Dms.Application.Documents;

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
        group.MapGet("/", async (
            DraftCreationService service,
            Guid? siteId,
            Guid? departmentId,
            Guid? documentTypeId,
            CancellationToken ct) =>
            Results.Ok(await service.ListAsync(siteId, departmentId, documentTypeId, ct)));

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
