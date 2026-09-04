using Dms.Application.Signing;

namespace Dms.Api.Endpoints;

/// <summary>
/// The countersignature queue and the policy that fills it.
/// <para>
/// Separate from SigningEndpoints, which covers document approval. The two are different
/// mechanisms — one binds a signature to a content hash, the other to an act — and sharing a
/// route group would suggest an interchangeability that does not exist.
/// </para>
/// </summary>
public static class PendingActionEndpoints
{
    public static void MapPendingActionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/pending-actions").WithTags("Countersignature");

        // The worklist. Not filtered to what the caller can countersign, deliberately: seeing
        // that something is waiting on a colleague is useful, and the countersign call refuses
        // on permission anyway. Filtering here would hide the queue's real depth.
        group.MapGet("/", async (
            ActionSignatureService service,
            CancellationToken ct) =>
        {
            var result = await service.ListAwaitingAsync(ct);

            return result.ToHttpResult(actions =>
                Results.Ok(actions.Select(PendingActionView.From).ToList()));
        });

        group.MapPost("/{id:guid}/countersign", async (
            ActionSignatureService service,
            Guid id,
            CountersignRequest request,
            CancellationToken ct) =>
        {
            var result = await service.CountersignAsync(
                id, request.Password, request.Approve, request.Reason, ct);

            return result.ToHttpResult(action => Results.Ok(PendingActionView.From(action)));
        });

        var policy = app.MapGroup("/api/signature-policy").WithTags("Countersignature");

        // Readable by any authenticated caller so a screen can tell someone a password will be
        // needed before they start, rather than after they have filled in a form.
        policy.MapGet("/", async (
            ActionSignatureService service,
            CancellationToken ct) =>
            (await service.GetPolicyAsync(ct)).ToHttpResult());

        policy.MapPut("/", async (
            ActionSignatureService service,
            UpdateSignaturePolicyRequest request,
            CancellationToken ct) =>
            (await service.UpdatePolicyAsync(request, ct)).ToHttpResult());
    }
}
