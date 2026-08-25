using Dms.Api;
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
            .AllowAnonymous()
            // Per-IP limit, complementing the per-account lockout in AuthService. Lockout stops
            // many guesses at one account; this stops one password sprayed across many.
            .RequireRateLimiting(RateLimiting.LoginPolicy);

        group.MapGet("/me", (ICurrentUser currentUser) =>
            string.IsNullOrWhiteSpace(currentUser.UserName)
                ? Results.Unauthorized()
                : Results.Ok(new { userName = currentUser.UserName }));

        // Changing a password lives at /api/users/me/change-password, not here. It is
        // self-service with no administrator reset, because a password an administrator can
        // set is a credential a second person knows — which would let them sign as someone
        // else. Keeping it on the user endpoints keeps that reasoning in one place.
    }
}
