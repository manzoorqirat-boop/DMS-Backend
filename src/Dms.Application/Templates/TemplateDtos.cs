using Dms.Domain.Entities;
using Dms.Domain.Enums;

namespace Dms.Application.Templates;

/// <summary>
/// A template upload. <see cref="Content"/> is the whole .docx in memory — acceptable because
/// the validator needs random access across the zip's central directory, and templates are
/// small documents by nature. The size ceiling in
/// <see cref="TemplateRegistrationService"/> is what keeps that honest.
/// </summary>
public sealed record RegisterTemplateRequest(Guid DocumentTypeId, string Name, byte[] Content);

/// <summary>
/// Read model for a registered template. Deliberately does not expose
/// <c>StorageKey</c> — the key is an internal address into the document store, and handing it
/// to a client invites exactly the direct-file-access pattern URS Functions #13 rules out.
/// </summary>
public sealed record TemplateSummary(
    Guid Id,
    Guid DocumentTypeId,
    string Name,
    int TemplateVersion,
    TemplateStatus Status,
    bool IsUsable,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ValidatedAt,
    IReadOnlyList<string> ValidationIssues)
{
    public static TemplateSummary From(DocumentTemplate template) => new(
        template.Id,
        template.DocumentTypeId,
        template.Name,
        template.TemplateVersion,
        template.Status,
        template.IsUsable,
        template.CreatedBy,
        template.CreatedAt,
        template.ValidatedAt,
        template.ValidationIssues);
}
