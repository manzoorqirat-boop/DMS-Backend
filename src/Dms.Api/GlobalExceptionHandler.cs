using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Dms.Api;

/// <summary>
/// Turns an unhandled exception into a ProblemDetails response carrying a trace id.
/// <para>
/// Every <i>expected</i> failure in this system is already a <c>Result</c> mapped to a clean
/// 400/404/409. Reaching here means something genuinely unforeseen happened — so the response
/// deliberately says nothing about what, beyond an id. A stack trace tells an attacker which
/// libraries and versions are in play; the trace id lets support find the full detail in the
/// logs, where it belongs.
/// </para>
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var traceId = httpContext.TraceIdentifier;

        // Logged at Error with the full exception and the request that caused it. This is the
        // only place the detail exists, so it has to be complete.
        logger.LogError(
            exception,
            "Unhandled exception on {Method} {Path}. TraceId {TraceId}.",
            httpContext.Request.Method,
            httpContext.Request.Path,
            traceId);

        // A cancelled request isn't a fault — the client went away. Nothing useful can be
        // written to a closed connection, so let the pipeline finish quietly.
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        await httpContext.Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "internal_error",
                Detail = "The request could not be completed. Quote the trace id when reporting this.",
                Extensions = { ["code"] = "internal_error", ["traceId"] = traceId },
            },
            cancellationToken);

        return true;
    }
}
