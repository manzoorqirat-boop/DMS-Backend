using Dms.Application.Abstractions;

namespace Dms.Infrastructure.Storage;

/// <summary>
/// Stands in when no document server is configured.
/// <para>
/// Exists so that services depending on <see cref="IDocumentConverter"/> can be registered
/// unconditionally. Registering the real converter only when a document server is present —
/// while its consumers were registered always — is precisely the mismatch that made the
/// container unresolvable for <c>EditingService</c> earlier in this project's life: a failure
/// that surfaced only when something happened to resolve the dependency, long after the
/// mistake was made.
/// </para>
/// <para>
/// <see cref="IsAvailable"/> is false, which callers are expected to check and degrade on;
/// <see cref="ToPdfAsync"/> throws rather than returning the input unconverted, because a
/// caller that asked for a PDF and silently received a .docx would store it under a .pdf name
/// and only discover the problem when someone tried to open it.
/// </para>
/// </summary>
public sealed class UnavailableDocumentConverter : IDocumentConverter
{
    public bool IsAvailable => false;

    public Task<byte[]> ToPdfAsync(byte[] docx, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "No document server is configured, so documents cannot be converted to PDF. "
            + "Set DocumentServer:Url, CallbackBaseUrl and TokenSecret to enable conversion.");
}
