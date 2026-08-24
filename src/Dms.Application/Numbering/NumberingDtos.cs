using Dms.Domain.Entities;

namespace Dms.Application.Numbering;

public sealed record CreateNumberingRuleRequest(Guid DocumentTypeId, Guid? SiteId, string Pattern);

public sealed record NumberingRuleView(
    Guid Id,
    Guid DocumentTypeId,
    string DocumentTypeCode,
    Guid? SiteId,
    string Pattern,
    string Scope,
    string CreatedBy,
    DateTimeOffset CreatedAt)
{
    public static NumberingRuleView From(NumberingRule rule, string documentTypeCode) => new(
        rule.Id,
        rule.DocumentTypeId,
        documentTypeCode,
        rule.SiteId,
        rule.Pattern,
        rule.SiteId is null ? "All sites" : "Site override",
        rule.CreatedBy,
        rule.CreatedAt);
}

/// <summary>What a pattern produces, shown to an administrator before they save it.</summary>
public sealed record PatternPreview(
    string Pattern,
    string FirstDocument,
    string LaterDocument,
    string ResetBehaviour);
