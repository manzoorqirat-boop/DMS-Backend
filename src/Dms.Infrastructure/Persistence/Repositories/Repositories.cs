using Dms.Application.Abstractions;
using Dms.Application.Common;
using Dms.Domain.Entities;
using Dms.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dms.Infrastructure.Persistence.Repositories;

/// <summary>
/// Turns a failed flush into a <see cref="PersistOutcome"/> when — and only when — the cause
/// is a unique-constraint violation. Everything else keeps throwing: a connection failure or
/// a check-constraint breach is a fault, not an expected branch, and swallowing it into a
/// tidy return value would hide real problems behind a 409.
/// </summary>
internal static class SaveChangesTranslator
{
    /// <summary>Postgres SQLSTATE for unique_violation.</summary>
    private const string UniqueViolation = "23505";

    public static async Task<PersistOutcome> SaveAsync(DbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return PersistOutcome.Success;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation } pg)
        {
            // The failed entities stay tracked in a modified state after a rejected flush; a
            // later save on the same scoped context would retry them. Detaching keeps the unit
            // of work clean for whatever the caller does next.
            foreach (var entry in db.ChangeTracker.Entries().ToList())
            {
                entry.State = EntityState.Detached;
            }

            return PersistOutcome.UniqueViolation(pg.ConstraintName);
        }
    }
}

public sealed class DocumentTypeRepository(DmsDbContext db) : IDocumentTypeRepository
{
    public Task<DocumentType?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.DocumentTypes.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<IReadOnlyList<DocumentType>> ListAsync(
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var query = db.DocumentTypes.AsQueryable();

        if (!includeInactive)
        {
            query = query.Where(x => x.IsActive);
        }

        return await query.OrderBy(x => x.Code).ToListAsync(cancellationToken);
    }

    public void Add(DocumentType documentType) => db.DocumentTypes.Add(documentType);

    public Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken) =>
        SaveChangesTranslator.SaveAsync(db, cancellationToken);
}

public sealed class TemplateRepository(DmsDbContext db) : ITemplateRepository
{
    public Task<DocumentTemplate?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.DocumentTemplates.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<DocumentTemplate?> GetActiveAsync(Guid documentTypeId, CancellationToken cancellationToken) =>
        db.DocumentTemplates.FirstOrDefaultAsync(
            x => x.DocumentTypeId == documentTypeId && x.Status == TemplateStatus.Active,
            cancellationToken);

    public async Task<int> GetHighestVersionAsync(Guid documentTypeId, CancellationToken cancellationToken)
    {
        // MaxAsync over an empty set throws; projecting to int? and taking the max of nothing
        // yields null, which is the "no versions yet" case rather than an error.
        var highest = await db.DocumentTemplates
            .Where(x => x.DocumentTypeId == documentTypeId)
            .Select(x => (int?)x.TemplateVersion)
            .MaxAsync(cancellationToken);

        return highest ?? 0;
    }

    public async Task<IReadOnlyList<DocumentTemplate>> ListAsync(
        Guid? documentTypeId,
        CancellationToken cancellationToken)
    {
        var query = db.DocumentTemplates.AsQueryable();

        if (documentTypeId is { } typeId)
        {
            query = query.Where(x => x.DocumentTypeId == typeId);
        }

        return await query
            .OrderBy(x => x.DocumentTypeId)
            .ThenByDescending(x => x.TemplateVersion)
            .ToListAsync(cancellationToken);
    }

    public void Add(DocumentTemplate template) => db.DocumentTemplates.Add(template);

    public Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken) =>
        SaveChangesTranslator.SaveAsync(db, cancellationToken);
}
