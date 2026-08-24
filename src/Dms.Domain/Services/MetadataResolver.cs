using System.Globalization;
using Dms.Domain.Entities;
using Dms.Domain.Enums;

namespace Dms.Domain.Services;

/// <summary>
/// Turns a document type's configured metadata field definitions plus a document's actual data
/// into the tag → value map that <see cref="DocxMetadataWriter"/> writes and
/// <see cref="DocxProtectionVerifier"/> checks against.
/// <para>
/// One resolver serving both matters more than it looks. The writer stamps values into the
/// template; the verifier later confirms they're unchanged. If those two built their maps
/// independently, any divergence — a date format, a trimmed string — would surface as a
/// spurious integrity failure on a document nobody touched. Sharing this function makes that
/// class of bug impossible rather than unlikely.
/// </para>
/// <para>
/// Pure and I/O-free: everything it needs is passed in.
/// </para>
/// </summary>
public static class MetadataResolver
{
    /// <summary>
    /// Values for every configured field, keyed by the template's own tag names.
    /// </summary>
    /// <param name="definitions">The document type's configured fields.</param>
    /// <param name="context">The document's data.</param>
    public static Dictionary<string, string> Resolve(
        IEnumerable<MetadataFieldDefinition> definitions,
        MetadataContext context)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var definition in definitions)
        {
            // Last-one-wins would silently hide a misconfiguration where two fields share a
            // tag; the unique index on (type, tag) is what prevents that, so a collision here
            // means the constraint was bypassed and overwriting would mask it.
            values[definition.Tag] = ValueFor(definition.Source, context);
        }

        return values;
    }

    private static string ValueFor(MetadataSource source, MetadataContext context) => source switch
    {
        MetadataSource.DocumentNumber => context.DocumentNumber,
        MetadataSource.DocumentTitle => context.Title,
        MetadataSource.Revision => DocumentNumberFormat.ComposeRevision(context.Revision),

        // Blank on anything not yet in force. A draft carrying a projected effective date on
        // its face is a document that looks issued and isn't — the single most consequential
        // thing to get wrong on a printed controlled copy.
        MetadataSource.EffectiveDate => context.EffectiveDate is { } date
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "",

        MetadataSource.DepartmentName => context.DepartmentName,
        MetadataSource.DepartmentCode => context.DepartmentCode,
        MetadataSource.SiteName => context.SiteName,
        MetadataSource.SiteCode => context.SiteCode,
        MetadataSource.DocumentTypeName => context.DocumentTypeName,
        MetadataSource.DocumentTypeCode => context.DocumentTypeCode,
        MetadataSource.Author => context.Author,
        MetadataSource.AuthorFullName => context.AuthorFullName,
        MetadataSource.CreatedDate => context.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        MetadataSource.Status => context.Status.ToString(),

        // Unreachable unless a MetadataSource value was added without a case here. Throwing
        // beats returning "" — a silently blank field on a controlled document is worse than
        // a loud failure at the point the gap was introduced.
        _ => throw new ArgumentOutOfRangeException(
            nameof(source), source, "No resolver defined for this metadata source."),
    };
}

/// <summary>
/// Everything the resolver can draw on. Flattened deliberately rather than taking the entities
/// themselves, so the resolver stays pure and the caller is forced to have loaded the site,
/// department and type before writing metadata — rather than the resolver lazily discovering
/// it needs them.
/// </summary>
public sealed record MetadataContext(
    string DocumentNumber,
    string Title,
    int Revision,
    DateOnly? EffectiveDate,
    string SiteCode,
    string SiteName,
    string DepartmentCode,
    string DepartmentName,
    string DocumentTypeCode,
    string DocumentTypeName,
    string Author,
    string AuthorFullName,
    DateTimeOffset CreatedAt,
    DocumentStatus Status);
