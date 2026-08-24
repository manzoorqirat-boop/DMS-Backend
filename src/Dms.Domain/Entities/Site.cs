using Dms.Domain.Common;

namespace Dms.Domain.Entities;

/// <summary>
/// A manufacturing or office location. First segment of a document number, and the top of the
/// department hierarchy the URS requires documents to be organised under.
/// <para>
/// Same shape and same reasoning as <see cref="DocumentType"/>: <see cref="Code"/> is baked
/// into every number already issued under it, so there's deliberately no rename for it.
/// </para>
/// </summary>
public class Site : Entity, ITimestamped
{
    private Site() { }

    public Site(string code, string name)
    {
        Code = NormalizeCode(code);
        Name = RequireName(name);
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public string Code { get; private set; } = "";
    public string Name { get; private set; } = "";
    public bool IsActive { get; private set; } = true;

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    public void Rename(string name)
    {
        Name = RequireName(name);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Deactivate()
    {
        IsActive = false;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Reactivate()
    {
        IsActive = true;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    internal static string NormalizeCode(string code) =>
        string.IsNullOrWhiteSpace(code)
            ? throw new ArgumentException("Site code is required.", nameof(code))
            : code.Trim().ToUpperInvariant();

    private static string RequireName(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Site name is required.", nameof(name))
            : name.Trim();
}
