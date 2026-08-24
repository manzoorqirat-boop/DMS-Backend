using Dms.Application.Abstractions;
using Dms.Application.Common;
using Dms.Domain.Entities;

namespace Dms.Application.Documents;

/// <summary>
/// Master-data maintenance for the site/department hierarchy documents are filed under.
/// Thin, like <c>DocumentTypeService</c>, and here for the same reason: a document number
/// needs a site and a department to exist before it can be composed.
/// </summary>
public sealed class OrganisationService(
    ISiteRepository sites,
    IDepartmentRepository departments,
    ICurrentUser currentUser)
{
    public async Task<Result<SiteSummary>> CreateSiteAsync(
        CreateSiteRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentUser.UserName))
        {
            return Error.Validation(
                "actor_unknown",
                "The acting user could not be determined. Master-data changes must be attributable.");
        }

        Site site;
        try
        {
            site = new Site(request.Code, request.Name);
        }
        catch (ArgumentException ex)
        {
            return Error.Validation("site_invalid", ex.Message);
        }

        sites.Add(site);
        var outcome = await sites.SaveChangesAsync(cancellationToken);

        if (!outcome.Saved)
        {
            return outcome.ViolatedIndexContains("code")
                ? Error.Conflict("site_code_taken", $"A site with code '{site.Code}' already exists.")
                : Error.Conflict("site_save_conflict", "The site could not be saved because of a conflicting concurrent change.");
        }

        return SiteSummary.From(site);
    }

    public async Task<IReadOnlyList<SiteSummary>> ListSitesAsync(
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var found = await sites.ListAsync(includeInactive, cancellationToken);
        return found.Select(SiteSummary.From).ToList();
    }

    public async Task<Result<DepartmentSummary>> CreateDepartmentAsync(
        CreateDepartmentRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentUser.UserName))
        {
            return Error.Validation(
                "actor_unknown",
                "The acting user could not be determined. Master-data changes must be attributable.");
        }

        var site = await sites.GetAsync(request.SiteId, cancellationToken);
        if (site is null)
        {
            return Error.NotFound("site_not_found", $"No site with id {request.SiteId}.");
        }

        Department department;
        try
        {
            department = new Department(site.Id, request.Code, request.Name);
        }
        catch (ArgumentException ex)
        {
            return Error.Validation("department_invalid", ex.Message);
        }

        departments.Add(department);
        var outcome = await departments.SaveChangesAsync(cancellationToken);

        if (!outcome.Saved)
        {
            return outcome.ViolatedIndexContains("site_code")
                ? Error.Conflict(
                    "department_code_taken",
                    $"Site '{site.Code}' already has a department with code '{department.Code}'.")
                : Error.Conflict(
                    "department_save_conflict",
                    "The department could not be saved because of a conflicting concurrent change.");
        }

        return DepartmentSummary.From(department);
    }

    public async Task<IReadOnlyList<DepartmentSummary>> ListDepartmentsAsync(
        Guid? siteId,
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var found = await departments.ListAsync(siteId, includeInactive, cancellationToken);
        return found.Select(DepartmentSummary.From).ToList();
    }
}
