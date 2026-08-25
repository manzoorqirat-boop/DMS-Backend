namespace Dms.Infrastructure.Persistence;

/// <summary>
/// Resolves the Postgres connection string from whichever form the hosting platform hands it
/// to us in.
/// <para>
/// Railway's Postgres plugin — and most platforms in that lineage (Heroku, Render) — exposes
/// the connection as a single <c>DATABASE_URL</c> environment variable in URI form:
/// <c>postgres://user:pass@host:port/database</c>. Npgsql doesn't accept that shape; it wants
/// keyword=value pairs (<c>Host=...;Port=...;Username=...</c>). Rather than asking whoever
/// deploys this to hand-translate one into the other — a step with no feedback until the app
/// fails to start — this resolver does the translation itself, and falls back to
/// <c>ConnectionStrings:Postgres</c> unchanged for any other environment (local dev, Docker
/// Compose, a platform that already speaks Npgsql's format).
/// </para>
/// </summary>
public static class DatabaseConnectionStringResolver
{
    public const string DatabaseUrlEnvironmentVariable = "DATABASE_URL";

    /// <summary>
    /// <paramref name="databaseUrl"/> is passed explicitly rather than read from
    /// <c>Environment.GetEnvironmentVariable</c> inside this method, so the translation logic
    /// itself is testable without setting process-wide environment state.
    /// </summary>
    public static string Resolve(string? databaseUrl, string? configuredConnectionString)
    {
        if (string.IsNullOrWhiteSpace(databaseUrl))
        {
            return configuredConnectionString
                ?? throw new InvalidOperationException(
                    "Neither DATABASE_URL nor ConnectionStrings:Postgres is configured.");
        }

        return TranslateDatabaseUrl(databaseUrl);
    }

    private static string TranslateDatabaseUrl(string databaseUrl)
    {
        // Uri parses "postgres://user:pass@host:port/db" and "postgresql://..." equally well —
        // the scheme name itself is never inspected below, only the parts after it.
        Uri uri;
        try
        {
            uri = new Uri(databaseUrl);
        }
        catch (UriFormatException ex)
        {
            throw new InvalidOperationException(
                $"{DatabaseUrlEnvironmentVariable} is set but isn't a valid connection URI.", ex);
        }

        var userInfo = uri.UserInfo.Split(':', 2);
        var username = Uri.UnescapeDataString(userInfo.ElementAtOrDefault(0) ?? "");
        var password = Uri.UnescapeDataString(userInfo.ElementAtOrDefault(1) ?? "");
        var database = uri.AbsolutePath.TrimStart('/');

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(database))
        {
            throw new InvalidOperationException(
                $"{DatabaseUrlEnvironmentVariable} is missing a username or database name.");
        }

        // SSL Mode=Require rather than Disable: Railway's Postgres accepts encrypted
        // connections, and defaulting to unencrypted on a managed cloud database — reachable,
        // for the public proxy variant, over the open internet — is the wrong default to ship.
        // Trust Server Certificate=true because Railway's certificate isn't in most trust
        // stores and the alternative is shipping a CA bundle for a certificate that rotates
        // outside this project's control.
        return string.Join(";",
            $"Host={uri.Host}",
            $"Port={(uri.Port > 0 ? uri.Port : 5432)}",
            $"Database={database}",
            $"Username={username}",
            $"Password={password}",
            "SSL Mode=Require",
            "Trust Server Certificate=true");
    }
}
