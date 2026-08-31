using System.Net.Http.Json;
using System.Text.Json;
using Dms.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Dms.Infrastructure.Storage;

/// <summary>
/// Converts .docx to PDF using the OnlyOffice Document Server's conversion service.
/// <para>
/// Extracted from <c>OnlyOfficePrintRenderer</c>, which previously owned this logic outright.
/// Two callers now need it — controlled printing and the approved-PDF export — and two copies
/// of a staging-plus-polling protocol would have drifted apart, with the second copy's bugs
/// only surfacing on whichever path was exercised less.
/// </para>
/// <para>
/// The conversion service fetches its source by URL rather than accepting an upload, so the
/// file is briefly published at a token-guarded public endpoint for it to collect. That is why
/// <c>DocumentServer:CallbackBaseUrl</c> must be the API as the document server sees it, not as
/// a browser sees it.
/// </para>
/// </summary>
public sealed class OnlyOfficeDocumentConverter(
    IHttpClientFactory httpClientFactory,
    IEditorSettings settings,
    IEditorTokenService tokens,
    IDocumentFileStore files,
    ILogger<OnlyOfficeDocumentConverter> logger) : IDocumentConverter
{
    public const string ClientName = "onlyoffice-converter";

    /// <summary>
    /// Prefix for the short-lived staging copies the conversion service fetches. Distinct from
    /// real document keys so a stray staging file can never be mistaken for a working copy,
    /// and so it is obvious what to sweep if one is ever orphaned.
    /// </summary>
    public const string StagingPrefix = "convert-staging/";

    public bool IsAvailable => settings.IsConfigured;

    public async Task<byte[]> ToPdfAsync(byte[] docx, CancellationToken cancellationToken)
    {
        if (!settings.IsConfigured)
        {
            throw new InvalidOperationException(
                "No document server is configured, so documents cannot be converted to PDF. "
                + "Set DocumentServer:Url, CallbackBaseUrl and TokenSecret.");
        }

        var client = httpClientFactory.CreateClient(ClientName);
        var conversionUrl = $"{settings.DocumentServerUrl.TrimEnd('/')}/ConvertService.ashx";

        // A per-conversion id, used as both the staging key and the document server's cache
        // key. Reusing one would return a previous conversion's PDF.
        var conversionId = Guid.NewGuid();
        var stagingKey = $"{StagingPrefix}{conversionId:N}.docx";

        try
        {
            await files.SaveAsync(stagingKey, docx, cancellationToken);

            var token = tokens.Issue(conversionId, DateTimeOffset.UtcNow.AddMinutes(5));
            var sourceUrl =
                $"{settings.CallbackBaseUrl.TrimEnd('/')}/api/public/editor/{token}/print-source";

            var request = new
            {
                async = false,
                filetype = "docx",
                outputtype = "pdf",
                key = conversionId.ToString("N"),
                title = $"{conversionId:N}.docx",
                url = sourceUrl,
            };

            // async:false asks the service to block until finished, but it may still answer
            // with partial progress under load, so this polls rather than assuming one call
            // suffices.
            for (var attempt = 0; attempt < 30; attempt++)
            {
                var response = await client.PostAsJsonAsync(conversionUrl, request, cancellationToken);
                response.EnsureSuccessStatusCode();

                var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

                if (payload.TryGetProperty("error", out var error))
                {
                    throw new InvalidOperationException(
                        $"The document server rejected the conversion (error {error}). Common "
                        + "causes: the staging URL isn't reachable from the document server "
                        + "(check DocumentServer:CallbackBaseUrl), or JWT is enabled there and "
                        + "the request isn't signed.");
                }

                if (payload.TryGetProperty("endConvert", out var done)
                    && done.ValueKind == JsonValueKind.True
                    && payload.TryGetProperty("fileUrl", out var fileUrl)
                    && fileUrl.GetString() is { Length: > 0 } address)
                {
                    return await client.GetByteArrayAsync(address, cancellationToken);
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }

            throw new TimeoutException(
                "The document server did not finish converting to PDF within 30 seconds.");
        }
        finally
        {
            // Always removed, success or failure. A failed conversion still leaves a full copy
            // of a controlled document behind a token-guarded public URL, and letting those
            // accumulate would be a slow leak of exactly the content this system controls.
            try
            {
                await files.DeleteAsync(stagingKey, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not remove conversion staging file {Key}.", stagingKey);
            }
        }
    }

    /// <summary>
    /// Reads back a staging copy for the document server. Keyed by the conversion id the token
    /// carries, so a valid signed token is the only way to reach it.
    /// </summary>
    public Task<byte[]?> ReadStagedAsync(Guid conversionId, CancellationToken cancellationToken) =>
        files.ReadAsync($"{StagingPrefix}{conversionId:N}.docx", cancellationToken);
}
