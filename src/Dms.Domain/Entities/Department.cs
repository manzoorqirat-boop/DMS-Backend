using Dms.Domain.Common;

namespace Dms.Domain.Entities;

/// <summary>
/// A department within a <see cref="Site"/> — QA, Production, Microbiology. Second segment of
/// a document number, and the owning unit a controlled document is filed under.
/// <para>
/// Scoped to a site rather than global: two sites may each have a "QA" and they are not the
/// same department, so the uniqueness constraint is on (site, code), not code alone.
/// </para>
/// </summary>
public class Department : Entity, ITimestamped
{
    private Department() { }

    public Department(Guid siteId, string code, string name)
    {
        SiteId = siteId != Guid.Empty
            ? siteId
            : throw new ArgumentException("Site is required.", nameof(siteId));
        Code = string.IsNullOrWhiteSpace(code)
            ? throw new ArgumentException("Department code is required.", nameof(code))
            : code.Trim().ToUpperInvariant();
        Name = RequireName(name);
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid SiteId { get; private set; }
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

    private static string RequireName(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Department name is required.", nameof(name))
            : name.Trim();
}
