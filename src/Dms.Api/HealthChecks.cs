using Dms.Application.Abstractions;
using Dms.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Dms.Domain.Common;

namespace Dms.Api;

/// <summary>
/// Confirms the database is reachable. Distinct from liveness: the process can be perfectly
/// alive while unable to serve a single request.
/// </summary>
public sealed class DatabaseHealthCheck(DmsDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy("Database reachable.")
                : HealthCheckResult.Unhealthy("Database is not reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database check failed.", ex);
        }
    }
}

/// <summary>
/// Confirms the blob store can actually be written to, not merely that a path was configured.
/// <para>
/// A readiness probe that only checks the database passes happily while every template upload
/// fails on a read-only or unmounted volume — which, given the store is a mounted volume in
/// every real deployment, is a realistic failure and an invisible one.
/// </para>
/// </summary>
public sealed class BlobStoreHealthCheck(ITemplateFileStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // Written under a reserved prefix and removed immediately. Named distinctly so a stray
        // probe file is never mistaken for a real template.
        var key = $"_healthcheck/{Uuid7.NewGuid():N}.probe";

        try
        {
            await store.SaveAsync(key, "probe"u8.ToArray(), cancellationToken);
            var read = await store.ReadAsync(key, cancellationToken);
            await store.DeleteAsync(key, cancellationToken);

            return read is { Length: > 0 }
                ? HealthCheckResult.Healthy("Blob store writable.")
                : HealthCheckResult.Unhealthy("Blob store accepted a write but returned nothing.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Blob store is not writable.", ex);
        }
    }
}

/// <summary>
/// Reports whether the document server is reachable.
/// <para>
/// <b>Degraded, never Unhealthy.</b> Editing is one feature; approving, distributing, printing
/// and the entire audit trail keep working without it. Failing readiness would pull a
/// functioning instance out of a load balancer over a feature most requests never touch.
/// </para>
/// </summary>
public sealed class DocumentServerHealthCheck(IEditorSettings settings, IHttpClientFactory factory)
    : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!settings.IsConfigured)
        {
            return HealthCheckResult.Healthy("No document server configured; in-browser editing is off.");
        }

        try
        {
            using var client = factory.CreateClient("health");
            client.Timeout = TimeSpan.FromSeconds(5);

            var response = await client.GetAsync(
                new Uri(new Uri(settings.DocumentServerUrl), "/healthcheck"), cancellationToken);

            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy("Document server reachable.")
                : HealthCheckResult.Degraded($"Document server returned {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded("Document server unreachable; in-browser editing will fail.", ex);
        }
    }
}
