using Dms.Application.Signing;

namespace Dms.Api.Endpoints;

/// <summary>
/// <b>Reconstructed file.</b> Present in the working codebase before this review but absent
/// from the uploaded archive. The <c>/me/change-password</c> route, the
/// <c>ChangePasswordRequest</c> call shape, the <c>/api/documents</c> "Review &amp; Approval"
/// group, and the <c>MakeEffectiveRequest</c> record are reproduced from fragments of the
/// original file quoted verbatim earlier in this project's history; the remaining routes are
/// filled in from <c>ReviewWorkflowService</c>'s public methods, which fully determine what
/// each endpoint must call and return.
/// </summary>
public static class SigningEndpoints
{
    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/users").WithTags("Users");

        group.MapGet("/", async (
            UserService service,
            bool? includeInactive,
            CancellationToken ct) =>
            (await service.ListAsync(includeInactive ?? false, ct)).ToHttpResult());

        group.MapPost("/", async (
            UserService service,
            CreateUserRequest request,
            CancellationToken ct) =>
        {
            var result = await service.CreateAsync(request, ct);
            return result.ToHttpResult(created => Results.Created($"/api/users/{created.Id}", created));
        });

        group.MapPost("/{id:guid}/deactivate", async (
            UserService service,
            Guid id,
            CancellationToken ct) =>
            (await service.DeactivateAsync(id, ct)).ToHttpResult());

        group.MapPost("/{id:guid}/reactivate", async (
            UserService service,
            Guid id,
            CancellationToken ct) =>
            (await service.ReactivateAsync(id, ct)).ToHttpResult());

        // Self-service only, and there is deliberately no administrator reset. A password an
        // administrator can set is a credential a second person knows, which would let them
        // sign as someone else.
        group.MapPost("/me/change-password", async (
            UserService service,
            ChangePasswordRequest request,
            CancellationToken ct) =>
            (await service.ChangeOwnPasswordAsync(request.CurrentPassword, request.NewPassword, ct))
                .ToHttpResult());
    }

    public static void MapReviewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/documents").WithTags("Review & Approval");

        // Locks the draft and starts its route. The route's shape comes from configuration;
        // the caller only nominates who fills each slot — see ReviewWorkflowService for why.
        group.MapPost("/{id:guid}/submit", async (
            ReviewWorkflowService service,
            Guid id,
            SubmitForReviewRequest request,
            CancellationToken ct) =>
            (await service.SubmitForReviewAsync(id, request, ct)).ToHttpResult());

        // §11.200: re-authenticates with the password at the moment of signing. Being logged
        // in is explicitly not sufficient.
        group.MapPost("/{id:guid}/sign", async (
            ReviewWorkflowService service,
            Guid id,
            SignRequest request,
            CancellationToken ct) =>
            (await service.SignAsync(id, request, ct)).ToHttpResult());

        group.MapGet("/{id:guid}/route", async (
            ReviewWorkflowService service,
            Guid id,
            CancellationToken ct) =>
            (await service.GetRouteAsync(id, ct)).ToHttpResult());

        // Brings an approved document into force. Deliberately distinct from "sign": issuance
        // is a dated act with its own consequences (supersession, retention, review scheduling)
        // rather than one more signature on the route.
        group.MapPost("/{id:guid}/effective", async (
            ReviewWorkflowService service,
            Guid id,
            MakeEffectiveRequest request,
            CancellationToken ct) =>
            (await service.MakeEffectiveAsync(id, request.EffectiveDate, ct)).ToHttpResult());

        // The caller's own signing queue — steps they can act on right now, not steps waiting
        // behind an earlier one.
        group.MapGet("/me/pending-signatures", async (
            ReviewWorkflowService service,
            CancellationToken ct) =>
            (await service.GetMyPendingAsync(ct)).ToHttpResult());
    }
}

public sealed record MakeEffectiveRequest(DateOnly EffectiveDate);

/// <summary>
/// Declared here rather than in Dms.Application.Auth: the acting service is
/// <see cref="UserService.ChangeOwnPasswordAsync"/>, so the request type lives with its
/// endpoint rather than beside the unrelated login/token flow in <c>AuthEndpoints</c>.
/// </summary>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
