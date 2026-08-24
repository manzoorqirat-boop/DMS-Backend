using Dms.Application.Abstractions;
using Dms.Application.Common;
using Dms.Domain.Entities;
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
        CancellationToken cancellationToken)
    {
        var query = db.ControlledDocuments.AsQueryable();

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
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dms.document_number_sequences (id, site_id, department_id, document_type_id, last_sequence)
            VALUES (@id, @site_id, @department_id, @document_type_id, 1)
            ON CONFLICT (site_id, department_id, document_type_id)
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
