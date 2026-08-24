using Dms.Application.Workflows;

namespace Dms.Api.Endpoints;

public static class WorkflowEndpoints
{
    public static void MapWorkflowEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/workflows").WithTags("Workflows");

        group.MapGet("/", async (
            WorkflowDefinitionService service,
            Guid? documentTypeId,
            CancellationToken ct) =>
            Results.Ok(await service.ListAsync(documentTypeId, ct)));

        group.MapPost("/", async (
            WorkflowDefinitionService service,
            CreateWorkflowRequest request,
            CancellationToken ct) =>
        {
            var result = await service.CreateAsync(request, ct);
            return result.ToHttpResult(created => Results.Created($"/api/workflows/{created.Id}", created));
        });

        // Steps can only be edited while the definition is inactive — otherwise the route
        // could change between a submitter loading the form and posting it.
        group.MapPut("/{id:guid}/steps", async (
            WorkflowDefinitionService service,
            Guid id,
            List<WorkflowStepRequest> steps,
            CancellationToken ct) =>
            (await service.SetStepsAsync(id, steps, ct)).ToHttpResult());

        group.MapPost("/{id:guid}/activate", async (
            WorkflowDefinitionService service,
            Guid id,
            CancellationToken ct) =>
            (await service.SetActiveAsync(id, isActive: true, ct)).ToHttpResult());

        group.MapPost("/{id:guid}/deactivate", async (
            WorkflowDefinitionService service,
            Guid id,
            CancellationToken ct) =>
            (await service.SetActiveAsync(id, isActive: false, ct)).ToHttpResult());

        // What a submission form renders: the fixed route shape plus, per step, the people
        // eligible to fill it. The submitter picks from these rather than typing a username.
        app.MapGet("/api/documents/{id:guid}/route-template", async (
            WorkflowDefinitionService service,
            Guid id,
            CancellationToken ct) =>
            (await service.ResolveTemplateAsync(id, ct)).ToHttpResult())
            .WithTags("Documents");
    }
}
