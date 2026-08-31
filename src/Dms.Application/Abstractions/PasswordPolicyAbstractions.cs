using Dms.Application.Common;
using Dms.Domain.Entities;

namespace Dms.Application.Abstractions;

/// <summary>
/// Reads and writes the single password policy row.
/// <para>
/// <see cref="GetAsync"/> never returns null: an installation with no policy row yet must
/// still enforce sensible rules rather than none at all, so the implementation seeds the
/// defaults on first read. A missing policy quietly meaning "no password rules" is precisely
/// the failure mode worth designing out.
/// </para>
/// </summary>
public interface IPasswordPolicyRepository
{
    Task<PasswordPolicy> GetAsync(CancellationToken cancellationToken);

    Task<PersistOutcome> SaveChangesAsync(CancellationToken cancellationToken);
}
