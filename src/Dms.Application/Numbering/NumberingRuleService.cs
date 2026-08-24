using Dms.Application.Abstractions;
using Dms.Application.Common;
using Dms.Domain.Entities;
using Dms.Domain.Enums;
using Dms.Domain.Services;

namespace Dms.Application.Numbering;

/// <summary>
/// Administers numbering patterns and resolves which one applies to a document being created.
/// <para>
/// This is what turns numbering from a code constant into master data. An administrator with
/// <see cref="Permission.NumberingConfigure"/> sets the pattern per document type, and
/// optionally overrides it per site.
/// </para>
/// </summary>
public sealed class NumberingRuleService(
    INumberingRuleRepository rules,
    IDocumentTypeRepository documentTypes,
    IAccessControl access,
    IAuditTrail audit,
    ICurrentUser currentUser)
{
    private const string EntityType = "NumberingRule";

    /// <summary>
    /// The pattern to use for a type at a site: the most specific matching rule, or the
    /// built-in default when none is configured.
    /// <para>
    /// Falling back to a default rather than failing is deliberate. A system that refuses to
    /// create documents until someone configures numbering is a system that can't be used out
    /// of the box, and the default is a valid, sensible pattern — not a placeholder.
    /// </para>
    /// </summary>
    public async Task<string> ResolvePatternAsync(
        Guid documentTypeId,
        Guid siteId,
        CancellationToken cancellationToken)
    {
        var candidates = await rules.FindCandidatesAsync(documentTypeId, siteId, cancellationToken);

        return candidates
            .OrderByDescending(r => r.Specificity)
            .Select(r => r.Pattern)
            .FirstOrDefault() ?? DocumentNumberPattern.Default;
    }

    public async Task<Result<NumberingRuleView>> CreateAsync(
        CreateNumberingRuleRequest request,
        CancellationToken cancellationToken)
    {
        var gate = await RequireConfigureAsync(request.SiteId, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var documentType = await documentTypes.GetAsync(request.DocumentTypeId, cancellationToken);
        if (documentType is null)
        {
            return Error.NotFound("document_type_not_found", $"No document type with id {request.DocumentTypeId}.");
        }

        NumberingRule rule;
        try
        {
            rule = new NumberingRule(
                request.DocumentTypeId, request.SiteId, request.Pattern, currentUser.UserName!);
        }
        catch (ArgumentException ex)
        {
            // The entity validates the pattern, so an invalid one can never reach the database.
            return Error.Validation("numbering_pattern_invalid", ex.Message);
        }

        rules.Add(rule);
        audit.Record(
            AuditAction.NumberingRuleCreated, EntityType, rule.Id,
            $"{documentType.Code}{(request.SiteId is null ? "" : " @site")}",
            $"Pattern '{rule.Pattern}'.");

        var outcome = await rules.SaveChangesAsync(cancellationToken);
        if (!outcome.Saved)
        {
            return outcome.ViolatedIndexContains("scope")
                ? Error.Conflict(
                    "numbering_rule_exists",
                    "A numbering rule already exists for that document type and site. Edit it instead.")
                : Error.Conflict("numbering_rule_save_conflict", "The numbering rule could not be saved.");
        }

        return NumberingRuleView.From(rule, documentType.Code);
    }

    public async Task<Result<NumberingRuleView>> ChangePatternAsync(
        Guid ruleId,
        string pattern,
        CancellationToken cancellationToken)
    {
        var rule = await rules.GetAsync(ruleId, cancellationToken);
        if (rule is null)
        {
            return Error.NotFound("numbering_rule_not_found", $"No numbering rule with id {ruleId}.");
        }

        var gate = await RequireConfigureAsync(rule.SiteId, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var previous = rule.Pattern;

        try
        {
            rule.ChangePattern(pattern);
        }
        catch (ArgumentException ex)
        {
            return Error.Validation("numbering_pattern_invalid", ex.Message);
        }

        var documentType = await documentTypes.GetAsync(rule.DocumentTypeId, cancellationToken);

        audit.Record(
            AuditAction.NumberingRuleChanged, EntityType, rule.Id,
            documentType?.Code ?? rule.DocumentTypeId.ToString(),
            $"Pattern '{previous}' → '{rule.Pattern}'. Numbers already issued are unchanged.");

        var outcome = await rules.SaveChangesAsync(cancellationToken);
        return outcome.Saved
            ? NumberingRuleView.From(rule, documentType?.Code ?? "")
            : Error.Conflict("numbering_rule_save_conflict", "The numbering rule could not be updated.");
    }

    public async Task<IReadOnlyList<NumberingRuleView>> ListAsync(
        Guid? documentTypeId,
        CancellationToken cancellationToken)
    {
        var found = await rules.ListAsync(documentTypeId, cancellationToken);
        var types = await documentTypes.ListAsync(includeInactive: true, cancellationToken);
        var codes = types.ToDictionary(t => t.Id, t => t.Code);

        return found
            .Select(r => NumberingRuleView.From(r, codes.GetValueOrDefault(r.DocumentTypeId, "")))
            .ToList();
    }

    /// <summary>
    /// Previews what a pattern produces without creating anything, so an administrator can see
    /// <c>MNK-QA-SOP-0001</c> before committing to the pattern that generates it. Validation
    /// errors surface here rather than at the first real document.
    /// </summary>
    public static Result<PatternPreview> Preview(string pattern)
    {
        var validation = DocumentNumberPattern.Validate(pattern);
        if (!validation.IsValid)
        {
            return Error.Validation("numbering_pattern_invalid", string.Join(" ", validation.Issues));
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var sample = DocumentNumberPattern.Render(
            pattern, new NumberTokens("MNK", "QA", "SOP", 1, 0, today));
        var later = DocumentNumberPattern.Render(
            pattern, new NumberTokens("MNK", "QA", "SOP", 42, 2, today));

        return Result<PatternPreview>.Success(new PatternPreview(
            pattern,
            sample,
            later,
            DocumentNumberPattern.PeriodKeyFor(pattern, today) is { Length: > 0 } period
                ? $"Sequence restarts each period (current period: {period})."
                : "Sequence runs continuously and never restarts."));
    }

    private async Task<Error?> RequireConfigureAsync(Guid? siteId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentUser.UserName))
        {
            return Error.Validation("actor_unknown", "The acting user could not be determined.");
        }

        // A site-scoped rule can be managed by a site-scoped administrator; a rule that applies
        // to every site requires the unscoped grant.
        var allowed = await access.HasPermissionAsync(
            Permission.NumberingConfigure, siteId, departmentId: null, cancellationToken);

        return allowed
            ? null
            : Error.Validation(
                "permission_denied",
                $"{Permission.NumberingConfigure} is required at this scope to configure numbering.");
    }
}
