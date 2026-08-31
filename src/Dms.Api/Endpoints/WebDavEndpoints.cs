using System.Text;
using System.Xml.Linq;
using Dms.Application.Editing;

namespace Dms.Api.Endpoints;

/// <summary>
/// A minimal WebDAV surface, sufficient for Microsoft Word to open a document, hold it, and
/// save it back.
/// <para>
/// <b>This path places a controlled file on a workstation.</b> URS #13 originally forbade that
/// outright, and the in-browser editor exists precisely so it never happened; desktop-Word
/// editing was subsequently agreed as a requirement, and desktop Word cannot work any other
/// way. Worth being explicit about what that costs, because the code can't mitigate it: once
/// Word has the file it lives in the user's temp and cache directories, it remains there after
/// they finish, and DMS has no means of removing it. Integrity is enforced on the return trip
/// instead — the PUT below runs exactly the same protection and metadata verification as the
/// in-browser save path, so a tampered file is caught and quarantined. That is detection, not
/// prevention, and the two are not equivalent.
/// </para>
/// <para>
/// Deliberately not a general-purpose WebDAV server. It serves exactly one file per URL, keyed
/// by a signed short-lived token that already identifies an active <c>EditingSession</c>, so
/// authorisation is the check-out itself rather than a separate scheme. Word's WebClient
/// service is notoriously unreliable about forwarding Authorization headers, which is why the
/// token travels in the path — the same pattern the OnlyOffice integration already uses for
/// its server-to-server fetches.
/// </para>
/// <para>
/// Only the verbs Word actually needs are implemented: OPTIONS (capability probe), HEAD and
/// PROPFIND (metadata), GET (open), LOCK/UNLOCK (Word insists before it will edit rather than
/// open read-only), and PUT (save). Anything else returns 405 rather than pretending to be a
/// complete WebDAV implementation.
/// </para>
/// </summary>
public static class WebDavEndpoints
{
    private const string DocxContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public static void MapWebDavEndpoints(this IEndpointRouteBuilder app)
    {
        // Anonymous by necessity: Word's WebClient does not carry the app's bearer token. The
        // token in the path is the credential, is signed, expires with the editing session,
        // and grants access to exactly one document.
        var dav = app.MapGroup("/api/public/webdav").WithTags("WebDAV").AllowAnonymous();

        // Word probes this before anything else and refuses to proceed if the DAV headers
        // aren't advertised. Class 2 specifically — class 1 means no locking, and Word will
        // then open the file read-only.
        dav.MapMethods("/{token}/{**fileName}", ["OPTIONS"], (HttpContext context) =>
        {
            context.Response.Headers["DAV"] = "1,2";
            context.Response.Headers["MS-Author-Via"] = "DAV";
            context.Response.Headers["Allow"] =
                "OPTIONS, HEAD, GET, PUT, PROPFIND, LOCK, UNLOCK";
            return Results.Ok();
        });

        dav.MapMethods("/{token}/{**fileName}", ["HEAD"], async (
            EditingService service,
            string token,
            HttpContext context,
            CancellationToken ct) =>
        {
            var result = await service.GetFileForWebDavAsync(token, ct);
            if (!result.IsSuccess)
            {
                return Results.NotFound();
            }

            context.Response.ContentType = DocxContentType;
            context.Response.ContentLength = result.Value.Content.Length;
            return Results.Ok();
        });

        // Word asks for the file's properties before opening it. The response is deliberately
        // minimal — name, size, type, and a resourcetype that says "not a collection" — which
        // is all Word consults for a single-file resource.
        dav.MapMethods("/{token}/{**fileName}", ["PROPFIND"], async (
            EditingService service,
            string token,
            string? fileName,
            HttpContext context,
            CancellationToken ct) =>
        {
            var result = await service.GetFileForWebDavAsync(token, ct);
            if (!result.IsSuccess)
            {
                return Results.NotFound();
            }

            var name = string.IsNullOrWhiteSpace(fileName) ? result.Value.FileName : fileName;
            var href = $"{context.Request.PathBase}{context.Request.Path}";
            var lastModified = result.Value.Session.LastActivityAt.UtcDateTime.ToString("R");

            XNamespace d = "DAV:";
            var response = new XDocument(
                new XElement(d + "multistatus",
                    new XAttribute(XNamespace.Xmlns + "D", d.NamespaceName),
                    new XElement(d + "response",
                        new XElement(d + "href", href),
                        new XElement(d + "propstat",
                            new XElement(d + "prop",
                                new XElement(d + "displayname", name),
                                new XElement(d + "getcontentlength", result.Value.Content.Length),
                                new XElement(d + "getcontenttype", DocxContentType),
                                new XElement(d + "getlastmodified", lastModified),
                                new XElement(d + "resourcetype")),
                            new XElement(d + "status", "HTTP/1.1 200 OK")))));

            // 207 Multi-Status, not 200 — Word treats a plain 200 here as a protocol error.
            context.Response.StatusCode = StatusCodes.Status207MultiStatus;
            context.Response.ContentType = "application/xml; charset=utf-8";
            await context.Response.WriteAsync(response.ToString(), Encoding.UTF8, ct);

            return Results.Empty;
        });

        dav.MapMethods("/{token}/{**fileName}", ["GET"], async (
            EditingService service,
            string token,
            CancellationToken ct) =>
        {
            var result = await service.GetFileForWebDavAsync(token, ct);

            return result.IsSuccess
                ? Results.File(result.Value.Content, DocxContentType, result.Value.FileName)
                : Results.NotFound();
        });

        // Word will not enter edit mode without a successful LOCK. The real lock is the
        // EditingSession that the token already represents — this returns a token shaped the
        // way Word expects rather than maintaining a second, parallel notion of who holds the
        // document. Two lock systems over one resource would be a way for them to disagree.
        dav.MapMethods("/{token}/{**fileName}", ["LOCK"], async (
            EditingService service,
            string token,
            HttpContext context,
            CancellationToken ct) =>
        {
            var result = await service.GetFileForWebDavAsync(token, ct);
            if (!result.IsSuccess)
            {
                // 423 Locked is what Word expects when someone else holds it, and produces a
                // far clearer message than a generic failure.
                return Results.StatusCode(StatusCodes.Status423Locked);
            }

            var session = result.Value.Session;
            var lockToken = $"opaquelocktoken:{session.Id:D}";
            var timeoutSeconds = Math.Max(60, (int)(session.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds);

            XNamespace d = "DAV:";
            var response = new XDocument(
                new XElement(d + "prop",
                    new XAttribute(XNamespace.Xmlns + "D", d.NamespaceName),
                    new XElement(d + "lockdiscovery",
                        new XElement(d + "activelock",
                            new XElement(d + "locktype", new XElement(d + "write")),
                            new XElement(d + "lockscope", new XElement(d + "exclusive")),
                            new XElement(d + "depth", "0"),
                            new XElement(d + "owner", session.UserName),
                            new XElement(d + "timeout", $"Second-{timeoutSeconds}"),
                            new XElement(d + "locktoken",
                                new XElement(d + "href", lockToken))))));

            context.Response.Headers["Lock-Token"] = $"<{lockToken}>";
            context.Response.ContentType = "application/xml; charset=utf-8";
            await context.Response.WriteAsync(response.ToString(), Encoding.UTF8, ct);

            return Results.Empty;
        });

        // Accepted and ignored: releasing the DAV lock does not release the check-out. That is
        // intentional — Word unlocks whenever it closes the file, including a crash or an
        // accidental close, and a check-out that evaporates on those is a check-out nobody can
        // rely on. The session ends when the user releases it or it expires.
        dav.MapMethods("/{token}/{**fileName}", ["UNLOCK"], () =>
            Results.StatusCode(StatusCodes.Status204NoContent));

        dav.MapMethods("/{token}/{**fileName}", ["PUT"], async (
            EditingService service,
            string token,
            HttpContext context,
            CancellationToken ct) =>
        {
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, ct);

            var result = await service.SaveFromWebDavAsync(token, buffer.ToArray(), ct);

            if (result.IsSuccess)
            {
                return Results.StatusCode(StatusCodes.Status204NoContent);
            }

            // 409 rather than 400: Word surfaces a conflict as "the file has been changed on
            // the server" and keeps the user's copy open, which is the right outcome when a
            // save is rejected for failing integrity checks — their work is not lost, and the
            // rejection is already recorded in the audit trail.
            return Results.Problem(
                title: result.Error!.Code,
                detail: result.Error.Message,
                statusCode: StatusCodes.Status409Conflict);
        });
    }
}
