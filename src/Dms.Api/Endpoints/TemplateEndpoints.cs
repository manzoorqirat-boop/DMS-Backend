using Dms.Application.Common;
using Dms.Application.DocumentTypes;
using Dms.Application.Templates;

namespace Dms.Api.Endpoints;

public static class TemplateEndpoints
{
    private const string DocxContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public static void MapDocumentTypeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/document-types").WithTags("Document Types");

        group.MapGet("/", async (
            DocumentTypeService service,
            bool? includeInactive,
            CancellationToken ct) =>
            // Nullable so the query string parameter is genuinely optional — a non-nullable
            // bool here would make ?includeInactive= mandatory and 400 without it.
            Results.Ok(await service.ListAsync(includeInactive ?? false, ct)));

        group.MapPost("/", async (
            DocumentTypeService service,
            CreateDocumentTypeRequest request,
            CancellationToken ct) =>
        {
            var result = await service.CreateAsync(request, ct);
            return result.ToHttpResult(created =>
                Results.Created($"/api/document-types/{created.Id}", created));
        });

        group.MapPost("/{id:guid}/deactivate", async (
            DocumentTypeService service,
            Guid id,
            CancellationToken ct) =>
            (await service.SetActiveAsync(id, isActive: false, ct)).ToHttpResult());

        group.MapPost("/{id:guid}/reactivate", async (
            DocumentTypeService service,
            Guid id,
            CancellationToken ct) =>
            (await service.SetActiveAsync(id, isActive: true, ct)).ToHttpResult());
    }

    public static void MapTemplateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/templates").WithTags("Templates");

        group.MapGet("/", async (
            TemplateRegistrationService service,
            Guid? documentTypeId,
            int? page,
            int? pageSize,
            CancellationToken ct) =>
            Results.Ok(await service.ListAsync(documentTypeId, new PagedRequest(page, pageSize), ct)));

        group.MapGet("/{id:guid}", async (
            TemplateRegistrationService service,
            Guid id,
            CancellationToken ct) =>
            (await service.GetAsync(id, ct)).ToHttpResult());

        // Multipart rather than JSON: the payload is a binary .docx, and base64-in-JSON would
        // inflate it by a third for no benefit.
        group.MapPost("/", async (
            TemplateRegistrationService service,
            HttpRequest request,
            CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new
                {
                    code = "multipart_expected",
                    detail = "Send the template as multipart/form-data with fields: documentTypeId, name, file.",
                });
            }

            var form = await request.ReadFormAsync(ct);

            if (!Guid.TryParse(form["documentTypeId"], out var documentTypeId))
            {
                return Results.BadRequest(new
                {
                    code = "document_type_id_invalid",
                    detail = "documentTypeId must be a GUID.",
                });
            }

            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(new
                {
                    code = "template_file_required",
                    detail = "A non-empty 'file' part is required.",
                });
            }

            if (file.Length > TemplateRegistrationService.MaxTemplateBytes)
            {
                return Results.BadRequest(new
                {
                    code = "template_file_too_large",
                    detail = $"Template exceeds the {TemplateRegistrationService.MaxTemplateBytes / (1024 * 1024)} MB limit.",
                });
            }

            // Name defaults to the uploaded filename rather than being mandatory on the form —
            // one less thing for an admin to fill in, and the value is only a label.
            var name = form["name"].ToString();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = Path.GetFileNameWithoutExtension(file.FileName);
            }

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);

            var result = await service.RegisterAsync(
                new RegisterTemplateRequest(documentTypeId, name, buffer.ToArray()), ct);

            // 201 even when structural validation failed: the registration itself succeeded
            // and produced a record to inspect. The caller reads Status and ValidationIssues
            // to see whether it's activatable — conflating "we stored your failed template"
            // with "your request was malformed" would lose that distinction.
            return result.ToHttpResult(created =>
                Results.Created($"/api/templates/{created.Id}", created));
        })
        .DisableAntiforgery();

        group.MapPost("/{id:guid}/activate", async (
            TemplateRegistrationService service,
            Guid id,
            CancellationToken ct) =>
            (await service.ActivateAsync(id, ct)).ToHttpResult());

        group.MapPost("/{id:guid}/retire", async (
            TemplateRegistrationService service,
            Guid id,
            CancellationToken ct) =>
            (await service.RetireAsync(id, ct)).ToHttpResult());

        group.MapGet("/{id:guid}/file", async (
            TemplateRegistrationService service,
            Guid id,
            CancellationToken ct) =>
        {
            var result = await service.DownloadAsync(id, ct);
            return result.ToHttpResult(file =>
                Results.File(file.Content, DocxContentType, file.FileName));
        });
    }
}
