using Dms.Domain.Common;

namespace Dms.Domain.Entities;

/// <summary>
/// The running counter behind the last segment of a document number, one row per
/// (site, department, document type).
/// <para>
/// This entity exists mainly so the counter has a table and EF configuration. The increment
/// itself is deliberately <b>not</b> performed by loading this entity, adding one, and saving:
/// that's a read-modify-write, and two authors creating an SOP in the same department at the
/// same moment would both read the same value. Allocation goes through a single atomic
/// UPSERT statement in the repository instead — see the numbering service for the full
/// reasoning.
/// </para>
/// </summary>
public class DocumentNumberSequence : Entity
{
    private DocumentNumberSequence() { }

    public DocumentNumberSequence(Guid siteId, Guid departmentId, Guid documentTypeId)
    {
        SiteId = siteId;
        DepartmentId = departmentId;
        DocumentTypeId = documentTypeId;
        LastSequence = 0;
    }

    public Guid SiteId { get; private set; }
    public Guid DepartmentId { get; private set; }
    public Guid DocumentTypeId { get; private set; }

    /// <summary>Highest sequence number issued so far for this combination. 0 means none yet.</summary>
    public int LastSequence { get; private set; }
}
