using Dms.Domain.Common;

namespace Dms.Domain.Entities;

/// <summary>
/// A category of controlled document — SOP, Protocol, Specification, Quality Manual, etc.
/// Source: URS Functions #1 (title uniqueness is scoped per type) and the review-level rules
/// the URS ties to type (3 levels for SOP, 4 for Protocol).
/// <para>
/// Deliberately thin master data. Review-level counts, site scoping, and numbering-pattern
/// details are left out of this entity for now — they belong to the numbering/workflow
/// phase of the build, not template registration. Growing this entity is easy later;
/// guessing its shape now and being wrong is not.
/// </para>
/// </summary>
public class DocumentType : Entity, ITimestamped
{
    private DocumentType() { }

    public DocumentType(string code, string name)
    {
        Code = NormalizeCode(code);
        Name = RequireName(name);
        CreatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Short, stable identifier used inside the document number — e.g. "SOP", "PROT".
    /// Uppercase, no spaces. Changing this after documents exist under it would break every
    /// number already issued, so there's deliberately no <c>Rename</c>-style method for it.
    /// </summary>
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

    /// <summary>
    /// Hides the type from "create new document" pickers without touching documents already
    /// created under it — deactivation is a going-forward decision, not a retroactive one.
    /// </summary>
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

    private static string NormalizeCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Document type code is required.", nameof(code));
        }

        return code.Trim().ToUpperInvariant();
    }

    private static string RequireName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Document type name is required.", nameof(name));
        }

        return name.Trim();
    }
}
