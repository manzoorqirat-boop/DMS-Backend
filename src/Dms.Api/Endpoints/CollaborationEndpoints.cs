using Dms.Application.Documents;

namespace Dms.Api.Endpoints;

/// <summary>
/// Global-document adoption and the collaborative draft review.
/// <para>
/// Grouped together because both concern a document before or beside its formal approval.
/// Neither involves the signature route, which lives in SigningEndpoints.
/// </para>
/// </summary>
public static class CollaborationEndpoints
{
    public static void MapCollaborationEndpoints(this IEndpointRouteBuilder app)
    {
        var documents = app.MapGroup("/api/documents").WithTags("Collaboration");

        // Scope lives here rather than with the other document writes because it exists only
        // to serve adoption — a local document never needs it set.
        documents.MapPost("/{id:guid}/scope", async (
            DraftCreationService service,
            Guid id,
            SetScopeRequest request,
            CancellationToken ct) =>
            (await service.SetScopeAsync(id, request, ct)).ToHttpResult());

        documents.MapPost("/{id:guid}/adoptions", async (
            AdoptionService service,
            Guid id,
            AdoptDocumentRequest request,
            CancellationToken ct) =>
            (await service.AdoptAsync(id, request, ct)).ToHttpResult());

        documents.MapGet("/{id:guid}/adoptions", async (
            AdoptionService service,
            Guid id,
            CancellationToken ct) =>
            (await service.ListForDocumentAsync(id, ct)).ToHttpResult());

        var adoptions = app.MapGroup("/api/adoptions").WithTags("Collaboration");

        adoptions.MapPost("/{id:guid}/withdraw", async (
            AdoptionService service,
            Guid id,
            WithdrawAdoptionRequest request,
            CancellationToken ct) =>
            (await service.WithdrawAsync(id, request, ct)).ToHttpResult());

        // Site is a route parameter rather than inferred from the caller: someone may act for
        // more than one site, and guessing would be wrong more often than right.
        adoptions.MapGet("/adoptable/{siteId:guid}", async (
            AdoptionService service,
            Guid siteId,
            CancellationToken ct) =>
            (await service.ListAdoptableAsync(siteId, ct)).ToHttpResult());

        documents.MapPost("/{id:guid}/draft-review/start", async (
            DraftReviewService service,
            Guid id,
            CancellationToken ct) =>
            (await service.StartAsync(id, ct)).ToHttpResult());

        documents.MapPost("/{id:guid}/draft-review/close", async (
            DraftReviewService service,
            Guid id,
            CancellationToken ct) =>
            (await service.CloseAsync(id, ct)).ToHttpResult());

        documents.MapGet("/{id:guid}/comments", async (
            DraftReviewService service,
            Guid id,
            CancellationToken ct) =>
            (await service.ListCommentsAsync(id, ct)).ToHttpResult());

        documents.MapPost("/{id:guid}/comments", async (
            DraftReviewService service,
            Guid id,
            AddReviewCommentRequest request,
            CancellationToken ct) =>
            (await service.AddCommentAsync(id, request, ct)).ToHttpResult());

        var comments = app.MapGroup("/api/comments").WithTags("Collaboration");

        comments.MapPost("/{id:guid}/respond", async (
            DraftReviewService service,
            Guid id,
            RespondToCommentRequest request,
            CancellationToken ct) =>
            (await service.RespondAsync(id, request, ct)).ToHttpResult());

        // Withdraw and reopen are both reviewer-only and differ by one flag, so they share a
        // handler rather than duplicating the lookup and the audit entry.
        comments.MapPost("/{id:guid}/withdraw", async (
            DraftReviewService service,
            Guid id,
            CancellationToken ct) =>
            (await service.ReviewerActionAsync(id, reopen: false, ct)).ToHttpResult());

        comments.MapPost("/{id:guid}/reopen", async (
            DraftReviewService service,
            Guid id,
            CancellationToken ct) =>
            (await service.ReviewerActionAsync(id, reopen: true, ct)).ToHttpResult());
    }
}
