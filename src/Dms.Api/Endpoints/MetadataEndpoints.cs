using Dms.Application.Metadata;
using Dms.Domain.Enums;

namespace Dms.Api.Endpoints;

public static class MetadataEndpoints
{
    public static void MapMetadataEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/metadata-fields").WithTags("Metadata Fields");

        // The values a field can be bound to. Read from the enum rather than a table, because
        // each one corresponds to real resolver code — offering a source nothing resolves
        // would produce a permanently blank control on every document of that type.
        group.MapGet("/sources", () => Results.Ok(
            Enum.GetValues<MetadataSource>()
                .Select(s => s.ToString())
                .OrderBy(s => s, StringComparer.Ordinal)));

        // Returns the type's configured fields, or the built-in default set when it has none
        // configured — so a caller always sees what will actually be written.
        group.MapGet("/", async (
            MetadataFieldService service,
            Guid documentTypeId,
            CancellationToken ct) =>
            Results.Ok(await service.ListAsync(documentTypeId, ct)));

        group.MapPost("/", async (
            MetadataFieldService service,
            CreateMetadataFieldRequest request,
            CancellationToken ct) =>
        {
            var result = await service.AddAsync(request, ct);
            return result.ToHttpResult(created =>
                Results.Created($"/api/metadata-fields/{created.Id}", created));
        });

        group.MapPut("/{id:guid}", async (
            MetadataFieldService service,
            Guid id,
            UpdateMetadataFieldRequest request,
            CancellationToken ct) =>
            (await service.UpdateAsync(id, request, ct)).ToHttpResult());

        group.MapDelete("/{id:guid}", async (
            MetadataFieldService service,
            Guid id,
            CancellationToken ct) =>
            (await service.RemoveAsync(id, ct)).ToHttpResult(_ => Results.NoContent()));
    }
}
