using Dms.Application.Access;
using Dms.Application.Numbering;
using Dms.Domain.Enums;

namespace Dms.Api.Endpoints;

public static class AccessEndpoints
{
    public static void MapRoleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/roles").WithTags("Roles");

        // The full list of grantable privileges, for rendering the matrix in an admin UI. Read
        // from the enum rather than a config table, because each value corresponds to a check
        // in code — a UI that offered a permission nothing enforces would be lying.
        group.MapGet("/permissions", () => Results.Ok(
            Enum.GetValues<Permission>()
                .Select(p => new { Name = p.ToString(), Value = (int)p })
                .OrderBy(p => p.Name, StringComparer.Ordinal)));

        group.MapGet("/", async (
            RoleService service,
            bool? includeInactive,
            CancellationToken ct) =>
            (await service.ListAsync(includeInactive ?? false, ct)).ToHttpResult());

        group.MapPost("/", async (
            RoleService service,
            CreateRoleRequest request,
            CancellationToken ct) =>
        {
            var result = await service.CreateAsync(request, ct);
            return result.ToHttpResult(created => Results.Created($"/api/roles/{created.Id}", created));
        });

        group.MapPut("/{id:guid}/permissions", async (
            RoleService service,
            Guid id,
            List<Permission> permissions,
            CancellationToken ct) =>
            (await service.SetPermissionsAsync(id, permissions, ct)).ToHttpResult());

        group.MapPost("/assignments", async (
            RoleService service,
            AssignRoleRequest request,
            CancellationToken ct) =>
        {
            var result = await service.AssignAsync(request, ct);
            return result.ToHttpResult(created =>
                Results.Created($"/api/roles/assignments/{created.Id}", created));
        });

        group.MapGet("/assignments", async (
            RoleService service,
            Guid? userId,
            Guid? roleId,
            CancellationToken ct) =>
            (await service.ListAssignmentsAsync(userId, roleId, ct)).ToHttpResult());

        group.MapDelete("/assignments/{id:guid}", async (
            RoleService service,
            Guid id,
            CancellationToken ct) =>
            (await service.RevokeAsync(id, ct)).ToHttpResult(_ => Results.NoContent()));

        // What the caller can do at a given scope. Drives menu visibility in a frontend —
        // though the server still enforces every check itself, since a hidden button is a UI
        // convenience and never a control.
        group.MapGet("/me/permissions", async (
            RoleService service,
            Guid? siteId,
            Guid? departmentId,
            CancellationToken ct) =>
            (await service.MyPermissionsAsync(siteId, departmentId, ct))
                .ToHttpResult(permissions => Results.Ok(permissions.Select(p => p.ToString()))));
    }

    public static void MapNumberingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/numbering-rules").WithTags("Numbering");

        group.MapGet("/", async (
            NumberingRuleService service,
            Guid? documentTypeId,
            CancellationToken ct) =>
            Results.Ok(await service.ListAsync(documentTypeId, ct)));

        // Preview before commit: shows what the pattern produces and whether the sequence
        // restarts, so the administrator sees MNK-QA-SOP-0001 before saving the rule.
        group.MapPost("/preview", (string pattern) =>
            NumberingRuleService.Preview(pattern).ToHttpResult());

        group.MapPost("/", async (
            NumberingRuleService service,
            CreateNumberingRuleRequest request,
            CancellationToken ct) =>
        {
            var result = await service.CreateAsync(request, ct);
            return result.ToHttpResult(created =>
                Results.Created($"/api/numbering-rules/{created.Id}", created));
        });

        group.MapPut("/{id:guid}/pattern", async (
            NumberingRuleService service,
            Guid id,
            string pattern,
            CancellationToken ct) =>
            (await service.ChangePatternAsync(id, pattern, ct)).ToHttpResult());
    }
}
