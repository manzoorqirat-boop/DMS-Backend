using Dms.Application.Abstractions;
using Dms.Application.Editing;
using Dms.Infrastructure.Storage;

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
        // AllowAnonymous is load-bearing here: the fallback authorization policy denies
        // everything by default, and the document server is a separate process with no user
        // session. The signed, expiring token in the path is its only credential.
        // Desktop Word. Deliberately a separate route from /edit rather than a flag on it:
        // this path puts the file on a workstation, which the in-browser path exists to avoid,
        // and that difference should be visible in the API surface rather than hidden behind
        // a parameter.
        documents.MapPost("/{id:guid}/edit/desktop", async (
            EditingService service,
            Guid id,
            CancellationToken ct) =>
            (await service.StartDesktopSessionAsync(id, ct)).ToHttpResult());

        // Read-only view. Separate from /edit because it takes no check-out and works at any
        // status — a reviewer reads documents that are deliberately not editable.
        documents.MapPost("/{id:guid}/view", async (
            EditingService service,
            Guid id,
            CancellationToken ct) =>
            (await service.StartViewSessionAsync(id, ct)).ToHttpResult());

        var public_ = app.MapGroup("/api/public/editor")
            .WithTags("Editing (document server)")
            .AllowAnonymous();

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

        // Staging source for the PDF conversion service. Token-guarded and short-lived (five
        // minutes), serving a file that is deleted the moment conversion finishes — see
        // OnlyOfficePrintRenderer.ConvertToPdfAsync.
        public_.MapGet("/{token}/print-source", async (
            IControlledPrintRenderer renderer,
            IEditorTokenService tokens,
            string token,
            CancellationToken ct) =>
        {
            if (renderer is not OnlyOfficePrintRenderer onlyOffice)
            {
                return Results.NotFound();
            }

            if (tokens.Validate(token) is not { } conversionId)
            {
                return Results.NotFound();
            }

            var content = await onlyOffice.ReadStagedAsync(conversionId, ct);

            return content is null
                ? Results.NotFound()
                : Results.File(
                    content,
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    $"{conversionId:N}.docx");
        });

        // Read-only counterpart of /{token}/file. No callback exists for this token, by
        // design: nothing served here can be written back.
        public_.MapGet("/{token}/view-file", async (
            EditingService service,
            string token,
            CancellationToken ct) =>
        {
            var result = await service.GetFileForViewerAsync(token, ct);

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
