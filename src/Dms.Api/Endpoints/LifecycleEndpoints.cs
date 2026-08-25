using Dms.Application.Documents;

namespace Dms.Api.Endpoints;

public static class LifecycleEndpoints
{
    public static void MapLifecycleEndpoints(this IEndpointRouteBuilder app)
    {
        var documents = app.MapGroup("/api/documents").WithTags("Documents");

        // Records that a review happened and the document is still correct as written. Pushes
        // the due date out by another interval, measured from today rather than from the old
        // due date, so a review completed late doesn't immediately fall due again.
        documents.MapPost("/{id:guid}/periodic-review", async (
            DocumentLifecycleService service,
            Guid id,
            PeriodicReviewRequest request,
            CancellationToken ct) =>
            (await service.RecordPeriodicReviewAsync(id, request.Outcome, ct)).ToHttpResult());

        // Withdraws from use with no replacement. Terminal, and not a delete — the record is
        // retained and anything citing it must still resolve.
        documents.MapPost("/{id:guid}/obsolete", async (
            DocumentLifecycleService service,
            Guid id,
            ObsoleteRequest request,
            CancellationToken ct) =>
            (await service.MakeObsoleteAsync(id, request.Reason, ct)).ToHttpResult());

        var reports = app.MapGroup("/api/reports").WithTags("Reports");

        // The pre-intimation report. Anything already overdue is included regardless of the
        // window and sorts to the top.
        reports.MapGet("/review-due", async (
            DocumentLifecycleService service,
            int? withinDays,
            Guid? siteId,
            Guid? departmentId,
            CancellationToken ct) =>
            Results.Ok(await service.ListDueForReviewAsync(withinDays ?? 90, siteId, departmentId, ct)));

        // Records the decision taken when retention expired, and carries it out. For
        // DestroyContent the stored file is deleted; the register row, its signatures and its
        // audit trail are kept.
        documents.MapPost("/{id:guid}/disposition", async (
            RetentionService service,
            Guid id,
            RecordDispositionRequest request,
            CancellationToken ct) =>
            (await service.RecordDispositionAsync(id, request.Action, request.Note, ct)).ToHttpResult());

        // Nothing acts on this automatically. Expiry makes a record eligible; a person decides.
        reports.MapGet("/disposition-due", async (
            RetentionService service,
            Guid? siteId,
            CancellationToken ct) =>
            Results.Ok(await service.ListDueForDispositionAsync(siteId, ct)));

        var retention = app.MapGroup("/api/retention-policies").WithTags("Retention Policies");

        retention.MapGet("/", async (
            RetentionService service,
            Guid? documentTypeId,
            CancellationToken ct) =>
            Results.Ok(await service.ListPoliciesAsync(documentTypeId, ct)));

        retention.MapPost("/", async (
            RetentionService service,
            CreateRetentionPolicyRequest request,
            CancellationToken ct) =>
        {
            var result = await service.CreatePolicyAsync(request, ct);
            return result.ToHttpResult(created =>
                Results.Created($"/api/retention-policies/{created.Id}", created));
        });

        retention.MapPut("/{id:guid}", async (
            RetentionService service,
            Guid id,
            UpdateRetentionPolicyRequest request,
            CancellationToken ct) =>
            (await service.UpdatePolicyAsync(id, request.RetentionYears, request.Trigger, ct)).ToHttpResult());

        var policies = app.MapGroup("/api/review-policies").WithTags("Review Policies");

        policies.MapGet("/", async (
            DocumentLifecycleService service,
            Guid? documentTypeId,
            CancellationToken ct) =>
            Results.Ok(await service.ListPoliciesAsync(documentTypeId, ct)));

        policies.MapPost("/", async (
            DocumentLifecycleService service,
            CreateReviewPolicyRequest request,
            CancellationToken ct) =>
        {
            var result = await service.CreatePolicyAsync(request, ct);
            return result.ToHttpResult(created =>
                Results.Created($"/api/review-policies/{created.Id}", created));
        });

        policies.MapPut("/{id:guid}", async (
            DocumentLifecycleService service,
            Guid id,
            UpdateReviewPolicyRequest request,
            CancellationToken ct) =>
            (await service.UpdatePolicyAsync(id, request.ReviewIntervalMonths, ct)).ToHttpResult());
    }
}

public sealed record PeriodicReviewRequest(string Outcome);

public sealed record ObsoleteRequest(string Reason);

public sealed record UpdateReviewPolicyRequest(int ReviewIntervalMonths);
