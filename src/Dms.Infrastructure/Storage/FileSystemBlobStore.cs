using Dms.Application.Abstractions;

namespace Dms.Infrastructure.Storage;

/// <summary>Configuration keys for the disk-backed template store.</summary>
public static class TemplateStorageConfig
{
    public const string SectionName = "TemplateStorage";

    public const string RootPathKey = $"{SectionName}:RootPath";

    /// <summary>
    /// Used when nothing is configured. Relative, so it resolves against the content root and
    /// stays inside the working tree during development.
    /// <para>
    /// On a container platform this must be overridden to point at a mounted persistent
    /// volume — a container filesystem is wiped on every redeploy, and losing the template a
    /// controlled document was created from is a data-integrity problem, not an inconvenience.
    /// </para>
    /// </summary>
    public const string DefaultRootPath = "storage/templates";
}

/// <summary>Configuration keys for the disk-backed working-copy store.</summary>
public static class DocumentStorageConfig
{
    public const string SectionName = "DocumentStorage";

    public const string RootPathKey = $"{SectionName}:RootPath";

    public const string DefaultRootPath = "storage/documents";
}

/// <summary>
/// Disk-backed blob storage. Straightforward on purpose: it exists so the build is runnable
/// end to end without standing up object storage, and so the seam for S3/MinIO is already in
/// the right place when that decision is made.
/// <para>
/// Templates and controlled documents get separate roots and separate interfaces, but share
/// this implementation — the containment and atomic-write logic below is exactly the kind of
/// thing that should not exist in two copies that can drift apart.
/// </para>
/// </summary>
public abstract class FileSystemBlobStore
{
    private readonly string _root;

    /// <summary>
    /// Takes the root as a plain string rather than an <c>IOptions&lt;T&gt;</c> so this
    /// assembly needs no options/binder package beyond what EF Core already brings —
    /// consistent with keeping each regulated repo's dependency surface minimal.
    /// </summary>
    protected FileSystemBlobStore(string rootPath)
    {
        _root = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(_root);
    }

    public async Task SaveAsync(string storageKey, byte[] content, CancellationToken cancellationToken)
    {
        var path = ResolvePath(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Write to a sibling temp file and move into place, so a process death mid-write
        // leaves no half-written .docx that would later validate as a corrupt zip.
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, content, cancellationToken);
        File.Move(temp, path, overwrite: true);
    }

    public async Task<byte[]?> ReadAsync(string storageKey, CancellationToken cancellationToken)
    {
        var path = ResolvePath(storageKey);
        return File.Exists(path)
            ? await File.ReadAllBytesAsync(path, cancellationToken)
            : null;
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        var path = ResolvePath(storageKey);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Maps a storage key to an absolute path and refuses anything that escapes the root.
    /// Keys are generated internally today, so this can't currently be reached with a hostile
    /// value — it's here because "the caller always passes a safe key" is the kind of
    /// assumption that quietly stops being true when a future feature accepts a key from a
    /// request.
    /// </summary>
    private string ResolvePath(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            throw new ArgumentException("Storage key is required.", nameof(storageKey));
        }

        var candidate = Path.GetFullPath(Path.Combine(_root, storageKey));

        var rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Storage key '{storageKey}' resolves outside the store root.");
        }

        return candidate;
    }
}

/// <summary>Template files. Root configured by <c>TemplateStorage:RootPath</c>.</summary>
public sealed class FileSystemTemplateFileStore(string rootPath)
    : FileSystemBlobStore(rootPath), ITemplateFileStore;

/// <summary>
/// Working copies of controlled documents. Root configured by <c>DocumentStorage:RootPath</c>,
/// separate from templates: different retention and different access rules, and a shared
/// bucket would be a harder split later than two from the start.
/// </summary>
public sealed class FileSystemDocumentFileStore(string rootPath)
    : FileSystemBlobStore(rootPath), IDocumentFileStore;
