using Dms.Application.Abstractions;
using Dms.Application.Common;
using Dms.Domain.Entities;
using Dms.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Dms.Infrastructure.Persistence.Repositories;

public sealed class UserRepository(DmsDbContext db) : IUserRepository
{
    public Task<DmsUser?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Users.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<DmsUser?> GetByUserNameAsync(string userName, CancellationToken cancellationToken)
    {
        // Lowercased to match how DmsUser normalises on construction — comparing here rather
        // than relying on a case-insensitive collation keeps the behaviour the same whatever
        // the database is configured with.
        var normalised = userName.Trim().ToLowerInvariant();
        return db.Users.FirstOrDefaultAsync(x => x.UserName == normalised, cancellationToken);
    }

    public async Task<IReadOnlyList<DmsUser>> ListAsync(bool includeInactive, CancellationToken cancellationToken)
    {
        var query = db.Users.AsQueryable();

        if (!includeInactive)
        {
            query = query.Where(x => x.IsActive);
        }

        return await query.OrderBy(x => x.UserName).ToListAsync(cancellationToken);
    }

    public void Add(DmsUser user) => db.Users.Add(user);

    public Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken) =>
        SaveChangesTranslator.SaveAsync(db, cancellationToken);
}

public sealed class SignatureRepository(DmsDbContext db) : ISignatureRepository
{
    public async Task<IReadOnlyList<SignatureRequest>> GetRouteAsync(
        Guid documentId,
        CancellationToken cancellationToken) =>
        await db.SignatureRequests
            .Where(x => x.DocumentId == documentId)
            .OrderBy(x => x.StepOrder)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SignatureRequest>> GetPendingForUserAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        await db.SignatureRequests
            .Where(x => x.UserId == userId && x.Status == SignatureRequestStatus.Pending)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SignatureRequest>> ListPendingForAllAsync(
        CancellationToken cancellationToken) =>
        await db.SignatureRequests
            .AsNoTracking()
            .Where(x => x.Status == SignatureRequestStatus.Pending)
            .OrderBy(x => x.DocumentId)
            .ThenBy(x => x.StepOrder)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ElectronicSignature>> GetSignaturesAsync(
        Guid documentId,
        CancellationToken cancellationToken) =>
        await db.ElectronicSignatures
            .AsNoTracking()
            .Where(x => x.DocumentId == documentId)
            .OrderBy(x => x.SignedAt)
            .ToListAsync(cancellationToken);

    public void AddRequest(SignatureRequest request) => db.SignatureRequests.Add(request);

    public void AddSignature(ElectronicSignature signature) => db.ElectronicSignatures.Add(signature);

    public Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken) =>
        SaveChangesTranslator.SaveAsync(db, cancellationToken);
}

/// <summary>
/// Signing policy read from configuration, with conservative defaults applied when it's absent.
/// </summary>
public sealed class SigningPolicy(int maxFailedAttempts, TimeSpan lockoutDuration) : ISigningPolicy
{
    public const string SectionName = "Signing";

    public const int DefaultMaxFailedAttempts = 3;

    public static readonly TimeSpan DefaultLockoutDuration = TimeSpan.FromMinutes(15);

    public int MaxFailedSigningAttempts { get; } = maxFailedAttempts > 0
        ? maxFailedAttempts
        : DefaultMaxFailedAttempts;

    public TimeSpan LockoutDuration { get; } = lockoutDuration > TimeSpan.Zero
        ? lockoutDuration
        : DefaultLockoutDuration;
}
