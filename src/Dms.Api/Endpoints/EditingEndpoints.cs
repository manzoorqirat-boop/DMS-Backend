using Dms.Application.Editing;

namespace Dms.Api.Endpoints;

public static class EditingEndpoints
{
    public static void MapEditingEndpoints(this IEndpointRouteBuilder app)
    {
        var documents = app.MapGroup("/api/documents").WithTags("Editing");

        // Checks the document out and returns what the browser needs to mount the editor.
        // Re-entrant for the holder — losing a browser tab shouldn't cost you your own lock.
        documents.MapPost("/{id:guid}/edit", async (
            EditingService service,
            Guid id,
            CancellationToken ct) =>
            (await service.StartSessionAsync(id, ct)).ToHttpResult());

        documents.MapPost("/{id:guid}/edit/release", async (
            EditingService service,
            Guid id,
            ReleaseLockRequest? request,
            CancellationToken ct) =>
            (await service.ReleaseAsync(id, request?.Note, ct)).ToHttpResult());

        documents.MapGet("/{id:guid}/edit/sessions", async (
            EditingService service,
            Guid id,
            CancellationToken ct) =>
            (await service.ListSessionsAsync(id, ct)).ToHttpResult());

        // Public routes: called by the document server, which is a separate process with no
        // user session. The signed, expiring token in the path is the only credential it can
        // present — see HmacEditorTokenService. Mounted under /api/public/ so a gateway can
        // route them without the normal authentication.
        var public_ = app.MapGroup("/api/public/editor").WithTags("Editing (document server)");

        public_.MapGet("/{token}/file", async (
            EditingService service,
            string token,
            CancellationToken ct) =>
        {
            var result = await service.GetFileForEditorAsync(token, ct);

            return result.ToHttpResult(file => Results.File(
                file.Content,
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                file.FileName));
        });

        // Always answers 200 with a body the document server understands; a non-zero error
        // makes it keep the file and retry. Returning an HTTP error status instead would make
        // it treat the save as permanently lost.
        public_.MapPost("/{token}/callback", async (
            EditingService service,
            string token,
            EditorCallback callback,
            CancellationToken ct) =>
        {
            var result = await service.HandleCallbackAsync(token, callback, ct);
            return Results.Ok(new { error = result.Error, message = result.Message });
        })
        .DisableAntiforgery();
    }
}

public sealed record ReleaseLockRequest(string? Note);
