using Dms.Application.Abstractions;

namespace Dms.Infrastructure.Storage;

/// <summary>
/// Stand-in renderer that returns the file unchanged.
/// <para>
/// Stamping a watermark across every page and flattening to PDF needs a document converter,
/// which is the same dependency the in-browser editor integration will bring. Rather than
/// block distribution on that, this returns <c>IsWatermarked = false</c> so the control layer
/// works end to end and the gap is <b>visible in every response and every audit entry</b>
/// rather than silently absent.
/// </para>
/// <para>
/// Replace this before any real controlled copy is printed. An unstamped page is
/// indistinguishable from an uncontrolled printout the moment it leaves the tray, which is
/// precisely what controlled printing exists to prevent.
/// </para>
/// </summary>
public sealed class PassThroughPrintRenderer : IControlledPrintRenderer
{
    private const string DocxContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public Task<PrintRenderResult> RenderAsync(
        byte[] source,
        string watermark,
        string scanCode,
        CancellationToken cancellationToken) =>
        Task.FromResult(new PrintRenderResult(source, DocxContentType, IsWatermarked: false));
}
