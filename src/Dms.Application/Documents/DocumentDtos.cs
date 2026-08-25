using Dms.Domain.Entities;
using Dms.Domain.Enums;

namespace Dms.Application.Documents;

public sealed record CreateDraftRequest(
    Guid SiteId,
    Guid DepartmentId,
    Guid DocumentTypeId,
    string Title);

/// <summary>
/// Read model for a controlled document. <c>WorkingCopyKey</c> is withheld for the same
/// reason a template's storage key is: it's an internal address, and exposing it invites the
/// direct-file-access pattern URS Functions #13 rules out.
/// </summary>
public sealed record DocumentSummary(
    Guid Id,
    string DocumentNumber,
    string Title,
    Guid SiteId,
    Guid DepartmentId,
    Guid DocumentTypeId,
    Guid TemplateId,
    int Revision,
    string RevisionLabel,
    Guid FamilyId,
    bool IsCurrentRevision,
    DocumentStatus Status,
    bool IsEditable,
    string Author,
    DateOnly? EffectiveDate,
    DateOnly? NextReviewDate,
    string? ObsoleteReason,
    DateOnly? RetainUntil,
    DispositionAction? Disposition,
    bool IsContentDestroyed,
    DateTimeOffset CreatedAt)
{
    public static DocumentSummary From(ControlledDocument document) => new(
        document.Id,
        document.DocumentNumber,
        document.Title,
        document.SiteId,
        document.DepartmentId,
        document.DocumentTypeId,
        document.TemplateId,
        document.Revision,
        Dms.Domain.Services.DocumentNumberFormat.ComposeRevision(document.Revision),
        document.FamilyId,
        document.IsCurrentRevision,
        document.Status,
        document.IsEditable,
        document.Author,
        document.EffectiveDate,
        document.NextReviewDate,
        document.ObsoleteReason,
        document.RetainUntil,
        document.Disposition,
        document.ContentDestroyedAt is not null,
        document.CreatedAt);
}

public sealed record CreateSiteRequest(string Code, string Name);

public sealed record SiteSummary(Guid Id, string Code, string Name, bool IsActive, DateTimeOffset CreatedAt)
{
    public static SiteSummary From(Site site) =>
        new(site.Id, site.Code, site.Name, site.IsActive, site.CreatedAt);
}

public sealed record CreateDepartmentRequest(Guid SiteId, string Code, string Name);

public sealed record DepartmentSummary(
    Guid Id,
    Guid SiteId,
    string Code,
    string Name,
    bool IsActive,
    DateTimeOffset CreatedAt)
{
    public static DepartmentSummary From(Department department) => new(
        department.Id,
        department.SiteId,
        department.Code,
        department.Name,
        department.IsActive,
        department.CreatedAt);
}

public sealed record IntegrityCheckResult(
    string DocumentNumber,
    bool IsValid,
    IReadOnlyList<string> Findings);
