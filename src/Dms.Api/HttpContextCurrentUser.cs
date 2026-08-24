using Dms.Application.Abstractions;

namespace Dms.Api;

/// <summary>
/// Resolves the acting user from the authenticated principal.
/// <para>
/// DMS has no auth model decided yet (see the build plan's open decisions), so today this
/// almost always returns null and every write is rejected with <c>actor_unknown</c> — which is
/// the correct default for a Part 11 system: no attributable identity, no record. The
/// development fallback below exists so Phase 1 can be exercised before auth lands, and is
/// deliberately awkward to switch on.
/// </para>
/// </summary>
public sealed class HttpContextCurrentUser(
    IHttpContextAccessor accessor,
    IConfiguration configuration,
    IHostEnvironment environment)
    : ICurrentUser
{
    /// <summary>Config key for the development-only impersonation escape hatch.</summary>
    public const string DevelopmentActorKey = "Development:ImpersonateUser";

    public string? UserName
    {
        get
        {
            var principalName = accessor.HttpContext?.User.Identity?.Name;
            if (!string.IsNullOrWhiteSpace(principalName))
            {
                return principalName;
            }

            // Gated on the hosting environment, not just on the setting being present, so
            // that shipping a stray appsettings value into production can't silently make
            // every record attributable to a fictional user.
            if (!environment.IsDevelopment())
            {
                return null;
            }

            var impersonated = configuration[DevelopmentActorKey];
            return string.IsNullOrWhiteSpace(impersonated) ? null : impersonated;
        }
    }
}
