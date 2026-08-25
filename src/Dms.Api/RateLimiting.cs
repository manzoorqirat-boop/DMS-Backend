using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Dms.Api;

/// <summary>
/// Request rate limiting.
/// <para>
/// Complements per-account lockout rather than duplicating it. Lockout stops many guesses
/// against <i>one</i> account; it does nothing about one password sprayed across a hundred
/// accounts, which is the attack that actually works against an organisation where someone
/// always has a weak password. That needs a limit on the caller, not on the target.
/// </para>
/// </summary>
public static class RateLimiting
{
    public const string LoginPolicy = "login";

    public const string EnabledKey = "RateLimiting:Enabled";
    public const string LoginPermitsKey = "RateLimiting:LoginAttemptsPerMinute";

    public static IServiceCollection AddDmsRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue(EnabledKey, true))
        {
            return services;
        }

        var loginPermits = Math.Clamp(configuration.GetValue(LoginPermitsKey, 10), 3, 120);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(LoginPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                // Partitioned by caller IP. Behind a reverse proxy this needs
                // UseForwardedHeaders configured, or every request appears to come from the
                // proxy and the whole estate shares one bucket — which would lock out real
                // users under any load at all.
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = loginPermits,
                    Window = TimeSpan.FromMinutes(1),

                    // No queue. A queued login attempt just arrives late; the caller should be
                    // told to slow down immediately.
                    QueueLimit = 0,
                }));

            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";

                await context.HttpContext.Response.WriteAsJsonAsync(
                    new
                    {
                        code = "rate_limited",
                        detail = "Too many attempts. Wait a minute and try again.",
                    },
                    cancellationToken);
            };
        });

        return services;
    }
}
