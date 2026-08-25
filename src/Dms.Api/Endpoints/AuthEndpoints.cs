using Dms.Application.Abstractions;
using Dms.Application.Auth;

namespace Dms.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Authentication");

        // Anonymous by necessity — this is where a token comes from.
        group.MapPost("/login", async (
            AuthService service,
            LoginRequest request,
            CancellationToken ct) =>
            (await service.LoginAsync(request, ct)).ToHttpResult())
            .AllowAnonymous();

        group.MapGet("/me", (ICurrentUser currentUser) =>
            string.IsNullOrWhiteSpace(currentUser.UserName)
                ? Results.Unauthorized()
                : Results.Ok(new { userName = currentUser.UserName }));

        // Requires the current password even though the caller is authenticated: the password
        // is also the signing credential, so an unattended session must not be enough to take
        // over someone's ability to sign.
        group.MapPost("/change-password", async (
            AuthService service,
            ICurrentUser currentUser,
            ChangePasswordRequest request,
            CancellationToken ct) =>
            (await service.ChangeOwnPasswordAsync(currentUser.UserName ?? "", request, ct))
                .ToHttpResult(_ => Results.NoContent()));
    }
}
