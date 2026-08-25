using Dms.Application.Abstractions;
using Dms.Application.Common;
using Dms.Domain.Entities;
using Dms.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Dms.Infrastructure.Persistence.Repositories;

public sealed class SiteRepository(DmsDbContext db) : ISiteRepository
{
    public Task<Site?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Sites.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Site>> ListAsync(bool includeInactive, CancellationToken cancellationToken)
    {
        var query = db.Sites.AsQueryable();

        if (!includeInactive)
        {
            query = query.Where(x => x.IsActive);
        }

        return await query.OrderBy(x => x.Code).ToListAsync(cancellationToken);
    }

    public void Add(Site site) => db.Sites.Add(site);

    public Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken) =>
        SaveChangesTranslator.SaveAsync(db, cancellationToken);
}

public sealed class DepartmentRepository(DmsDbContext db) : IDepartmentRepository
{
    public Task<Department?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Departments.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Department>> ListAsync(
        Guid? siteId,
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var query = db.Departments.AsQueryable();

        if (siteId is { } id)
        {
            query = query.Where(x => x.SiteId == id);
        }

        if (!includeInactive)
        {
            query = query.Where(x => x.IsActive);
        }

        return await query.OrderBy(x => x.Code).ToListAsync(cancellationToken);
    }

    public void Add(Department department) => db.Departments.Add(department);

    public Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken) =>
        SaveChangesTranslator.SaveAsync(db, cancellationToken);
}

public sealed class ControlledDocumentRepository(DmsDbContext db) : IControlledDocumentRepository
{
    public Task<ControlledDocument?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.ControlledDocuments.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<IReadOnlyList<ControlledDocument>> ListAsync(
        Guid? siteId,
        Guid? departmentId,
        Guid? documentTypeId,
        bool currentRevisionsOnly,
        CancellationToken cancellationToken)
    {
        var query = db.ControlledDocuments.AsQueryable();

        if (currentRevisionsOnly)
        {
            query = query.Where(x => x.IsCurrentRevision);
        }

        if (siteId is { } site)
        {
            query = query.Where(x => x.SiteId == site);
        }

        if (departmentId is { } department)
        {
            query = query.Where(x => x.DepartmentId == department);
        }

        if (documentTypeId is { } type)
        {
            query = query.Where(x => x.DocumentTypeId == type);
        }

        return await query.OrderBy(x => x.DocumentNumber).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ControlledDocument>> ListFamilyAsync(
        Guid familyId,
        CancellationToken cancellationToken) =>
        await db.ControlledDocuments
            .Where(x => x.FamilyId == familyId)
            .OrderBy(x => x.Revision)
            .ToListAsync(cancellationToken);

    public Task<ControlledDocument?> GetCurrentRevisionAsync(
        Guid familyId,
        CancellationToken cancellationToken) =>
        db.ControlledDocuments.FirstOrDefaultAsync(
            x => x.FamilyId == familyId && x.IsCurrentRevision, cancellationToken);

    public Task<ControlledDocument?> GetInFlightRevisionAsync(
        Guid familyId,
        CancellationToken cancellationToken) =>
        db.ControlledDocuments.FirstOrDefaultAsync(
            x => x.FamilyId == familyId
                && (x.Status == DocumentStatus.Draft || x.Status == DocumentStatus.InReview
                    || x.Status == DocumentStatus.Approved),
            cancellationToken);

    public async Task<IReadOnlyList<ControlledDocument>> ListDueForReviewAsync(
        DateOnly dueBy,
        Guid? siteId,
        Guid? departmentId,
        CancellationToken cancellationToken)
    {
        // Only Effective documents. A superseded or obsolete version has nothing to review,
        // and including them would bury the items that actually need action.
        var query = db.ControlledDocuments
            .Where(x => x.Status == DocumentStatus.Effective
                && x.NextReviewDate != null
                && x.NextReviewDate <= dueBy);

        if (siteId is { } site)
        {
            query = query.Where(x => x.SiteId == site);
        }

        if (departmentId is { } department)
        {
            query = query.Where(x => x.DepartmentId == department);
        }

        return await query.OrderBy(x => x.NextReviewDate).ToListAsync(cancellationToken);
    }

    public void Add(ControlledDocument document) => db.ControlledDocuments.Add(document);

    public Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken) =>
        SaveChangesTranslator.SaveAsync(db, cancellationToken);

    /// <summary>
    /// One statement, and deliberately so. <c>INSERT ... ON CONFLICT DO UPDATE ... RETURNING</c>
    /// creates the counter row on first use and increments it otherwise, atomically, taking a
    /// row lock that's held until the surrounding transaction ends. A second caller for the
    /// same (site, department, type) blocks on that lock rather than reading a stale value,
    /// which is what guarantees no two documents are handed the same number.
    /// <para>
    /// Written as raw SQL rather than through EF because there is no way to express
    /// "increment and return, atomically" in LINQ — <c>ExecuteUpdate</c> can't return the new
    /// value, and load-add-save is precisely the race this exists to avoid.
    /// </para>
    /// </summary>
    public async Task<int> AllocateNextSequenceAsync(
        Guid siteId,
        Guid departmentId,
        Guid documentTypeId,
        string periodKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dms.document_number_sequences
                (id, site_id, department_id, document_type_id, period_key, last_sequence)
            VALUES (@id, @site_id, @department_id, @document_type_id, @period_key, 1)
            ON CONFLICT (site_id, department_id, document_type_id, period_key)
            DO UPDATE SET last_sequence = dms.document_number_sequences.last_sequence + 1
            RETURNING last_sequence;
            """;

        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        // Enlists in the ambient EF transaction. Without this the statement would run on its
        // own and commit immediately, which would reintroduce exactly the gap this design
        // avoids: a rolled-back document insert would leave the counter advanced.
        if (db.Database.CurrentTransaction is { } current)
        {
            command.Transaction = current.GetDbTransaction();
        }

        command.Parameters.Add(new NpgsqlParameter("id", Guid.CreateVersion7()));
        command.Parameters.Add(new NpgsqlParameter("site_id", siteId));
        command.Parameters.Add(new NpgsqlParameter("department_id", departmentId));
        command.Parameters.Add(new NpgsqlParameter("document_type_id", documentTypeId));
        command.Parameters.Add(new NpgsqlParameter("period_key", periodKey));

        var result = await command.ExecuteScalarAsync(cancellationToken);

        return result is int sequence
            ? sequence
            : throw new InvalidOperationException("Sequence allocation returned no value.");
    }
}

/// <summary>
/// Runs an operation inside one database transaction.
/// <para>
/// Wrapped in the provider's execution strategy, which is not optional here: the DbContext is
/// configured with <c>EnableRetryOnFailure</c>, and EF refuses to start a user-initiated
/// transaction under a retrying strategy unless the whole unit of work is inside
/// <c>ExecuteAsync</c> — otherwise a retry would replay only part of it. The failure mode
/// without this is an exception on the first transactional write, not a subtle one, but it
/// only appears once retries are enabled and so is easy to write code that seems fine.
/// </para>
/// </summary>
public sealed class UnitOfWork(DmsDbContext db) : IUnitOfWork
{
    public Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();

        return strategy.ExecuteAsync(async ct =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var result = await operation(ct);

            await transaction.CommitAsync(ct);
            return result;
        }, cancellationToken);
    }
}

public sealed class DistributionRepository(DmsDbContext db) : IDistributionRepository
{
    public Task<DocumentDistribution?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.DocumentDistributions.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<IReadOnlyList<DocumentDistribution>> ListForDocumentAsync(
        Guid documentId,
        CancellationToken cancellationToken) =>
        await db.DocumentDistributions
            .Where(x => x.DocumentId == documentId)
            .OrderBy(x => x.CopyNumber)
            .ToListAsync(cancellationToken);

    public async Task<int> GetHighestCopyNumberAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var highest = await db.DocumentDistributions
            .Where(x => x.DocumentId == documentId)
            .Select(x => (int?)x.CopyNumber)
            .MaxAsync(cancellationToken);

        return highest ?? 0;
    }

    public async Task<IReadOnlyList<(DocumentDistribution Copy, ControlledDocument Document)>>
        ListPendingRetrievalAsync(Guid? siteId, CancellationToken cancellationToken)
    {
        // Joined to document status rather than maintaining a flag on the distribution: a
        // supersession then can't leave stale outstanding-copy state behind, because there is
        // no derived state to leave stale.
        var query =
            from copy in db.DocumentDistributions
            join document in db.ControlledDocuments on copy.DocumentId equals document.Id
            where copy.CopyType != CopyType.Uncontrolled
                && (copy.Status == DistributionStatus.Issued || copy.Status == DistributionStatus.Acknowledged)
                && (document.Status == DocumentStatus.Superseded || document.Status == DocumentStatus.Obsolete)
            select new { copy, document };

        if (siteId is { } site)
        {
            query = query.Where(x => x.document.SiteId == site);
        }

        var rows = await query
            .OrderBy(x => x.document.DocumentNumber)
            .ThenBy(x => x.copy.CopyNumber)
            .ToListAsync(cancellationToken);

        return rows.Select(x => (x.copy, x.document)).ToList();
    }

    public async Task<IReadOnlyList<DocumentDistribution>> ListUnacknowledgedBeforeAsync(
        DateTimeOffset issuedBefore,
        CancellationToken cancellationToken) =>
        await db.DocumentDistributions
            .AsNoTracking()
            .Where(x => x.Status == DistributionStatus.Issued
                && x.CopyType != CopyType.Uncontrolled
                && x.CreatedAt < issuedBefore)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

    public void Add(DocumentDistribution distribution) => db.DocumentDistributions.Add(distribution);

    public void AddPrintEvent(PrintEvent printEvent) => db.PrintEvents.Add(printEvent);

    public async Task<IReadOnlyList<PrintEvent>> ListPrintEventsAsync(
        Guid documentId,
        CancellationToken cancellationToken) =>
        await db.PrintEvents
            .AsNoTracking()
            .Where(x => x.DocumentId == documentId)
            .OrderByDescending(x => x.PrintedAt)
            .ToListAsync(cancellationToken);

    public Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken) =>
        SaveChangesTranslator.SaveAsync(db, cancellationToken);
}
